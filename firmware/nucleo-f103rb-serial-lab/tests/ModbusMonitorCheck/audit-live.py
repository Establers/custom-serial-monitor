"""Independently audit a fresh scenarios-live or commands-live evidence directory.

Uses only Python's standard library; never imports the application's packet code.
No device access. Existing audit outputs are not overwritten.
"""
import hashlib
import json
import sys
from pathlib import Path


def crc(data):
    value = 0xFFFF
    for byte in data:
        value ^= byte
        for _ in range(8):
            value = (value >> 1) ^ (0xA001 if value & 1 else 0)
    return value


def integer(data):
    return int.from_bytes(data, "big")


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def audit(root):
    report = load(root / "report.json")
    mode = report["report"]["mode"]
    assert mode in ("scenarios-live", "commands-live")
    scenarios = mode == "scenarios-live"
    journeys = scenarios and report["report"].get("scenarioContract") == "user-journeys-v2"
    expected_cases, expected_writes = ((15, 26) if journeys else (13, 12)) if scenarios else (16, 32)
    assert report["passed"] and report["report"]["hardwareVerified"]
    assert len(report["results"]) == expected_cases
    assert all(case["Status"] == "pass" for case in report["results"])
    assert not report["evidenceOverflow"] and not report["serialErrors"]
    exchanges = []
    for line in (root / "wire.jsonl").read_text(encoding="utf-8-sig").splitlines():
        entry = json.loads(line)
        payload = bytes.fromhex(entry["hex"])
        if entry["Direction"] == "TX":
            exchanges.append((entry["Case"], payload, bytearray()))
        else:
            assert entry["Direction"] == "RX" and exchanges, "Unsolicited/unaccounted RX"
            exchanges[-1][2].extend(payload)

    ram, coils = {}, {}
    initial_ram = initial_coils = None
    initial_scratch = None
    preset_writes = []
    preset_requests = []
    initial_counter = last_counter = None
    previous_reads = None
    previous_read_index = 0
    writes = reads = exceptions = text_requests = corrupt_acks = 0
    for case, request, response in exchanges:
        if request[0] != 1:
            text_requests += 1
            assert request.endswith(b"\r\n") and b"RESET" not in request
            command = request[:-2].decode("ascii")
            if command == "BUILD?":
                assert response == b"BUILD version=1.7.1 protocol=1 id=22F524185C55784C\r\n"
            elif command == "ID?":
                assert response == b"ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1\r\n"
            elif command == "RTU STATUS?":
                assert response.startswith(b"RTU reads=") and response.endswith(b"\r\n")
                assert b"fault=NONE" in response and b"pending=0" in response
            else:
                assert command in ("LAB", "RTU", "RTU FAULT CLEAR", "RTU FAULT DROP", "RTU FAULT CRC", "RTU FAULT DELAY 1200", "RTU FAULT DELAY 300")
                assert response == ("OK " + command + "\r\n").encode("ascii")
            continue

        assert len(request) >= 8 and crc(request) == 0, (case, "request CRC/length")
        function, address = request[1], integer(request[2:4])
        if case == "scenario-installed-presets-actual-execution":
            preset_requests.append(request.hex().upper())
        if response and response[1] == (function | 0x80):
            assert function in (3, 6) and address == 0x2000
            assert len(response) == 5 and crc(response) == 0 and response[:3] == bytes((1, function | 0x80, 2))
            exceptions += 1
            continue

        if function in (5, 6, 15, 16):
            writes += 1
            if function in (5, 6):
                assert len(request) == 8
                value = integer(request[4:6])
                assert function != 5 or value in (0, 0xFF00)
                values = [int(value != 0)] if function == 5 else [value]
            else:
                count = integer(request[4:6])
                size = (count + 7) // 8 if function == 15 else count * 2
                assert len(request) == size + 9 and request[6] == size
                values = [(request[7 + index // 8] >> (index % 8)) & 1 for index in range(count)] if function == 15 else [integer(request[7 + index * 2:9 + index * 2]) for index in range(count)]
            target = coils if function in (5, 15) else ram
            ranges = [(0x1000, 0x1010)] if target is coils else [(0x1000, 0x1004)]
            if journeys and target is ram:
                ranges.append((0x1010, 0x101A))
            assert any(start <= address and address + len(values) <= end for start, end in ranges)
            if case == "scenario-installed-presets-actual-execution":
                preset_writes.append((function, address, values))
            target.update({address + index: value for index, value in enumerate(values)})
            expected = request if function in (5, 6) else request[:6] + crc(request[:6]).to_bytes(2, "little")
            if case == "command-drop-write-commits-once" and not response:
                assert function == 6 and integer(request[4:6]) == 0x55AA
                continue
            if case == "command-crc-write-commits-once" and crc(response):
                expected = expected[:-1] + bytes((expected[-1] ^ 1,))
                corrupt_acks += 1
            assert response == expected, (case, "write ACK")
            continue

        assert function in (1, 2, 3, 4)
        count = integer(request[4:6])
        size = (count + 7) // 8 if function <= 2 else count * 2
        assert len(response) == size + 5 and response[:3] == bytes((1, function, size)) and crc(response) == 0, (case, "read CRC/length/function")
        values = [(response[3 + index // 8] >> (index % 8)) & 1 for index in range(count)] if function <= 2 else [integer(response[3 + index * 2:5 + index * 2]) for index in range(count)]
        if function <= 2 and count % 8:
            assert response[-3] >> (count % 8) == 0
        reads += 1
        if function == 3 and address == 0:
            assert values == [250, 100]
        if function == 3 and 0x1000 <= address < 0x1004:
            if initial_ram is None:
                assert address == 0x1000 and count == 4
                initial_ram = values.copy()
                ram.update({address + index: value for index, value in enumerate(values)})
            assert values == [ram[address + index] for index in range(count)]
        if journeys and function == 3 and 0x1010 <= address < 0x101A:
            if initial_scratch is None:
                assert address == 0x1010 and count == 10
                initial_scratch = values.copy()
                ram.update({address + index: value for index, value in enumerate(values)})
            assert values == [ram[address + index] for index in range(count)]
        if function == 1 and address == 0x1000:
            if initial_coils is None:
                initial_coils = values.copy()
                coils.update({address + index: value for index, value in enumerate(values)})
            assert values == [coils[address + index] for index in range(count)]
        if (function, address) in ((2, 0x1001), (4, 0x1007)):
            assert values == [coils[0x1000]]
        if function == 4 and address in (0x1000, 0x1002) and count >= (4 if address == 0x1000 else 2):
            offset = 2 if address == 0x1000 else 0
            last_counter = (values[offset] << 16) | values[offset + 1]
            if initial_counter is None:
                initial_counter = (last_counter - writes) & 0xFFFFFFFF
            assert (last_counter - initial_counter) & 0xFFFFFFFF == writes
        if function == 4 and address == 0x1000 and count >= 6:
            server_reads = (values[4] << 16) | values[5]
            if previous_reads is not None:
                assert (server_reads - previous_reads) & 0xFFFFFFFF == reads - previous_read_index
            previous_reads, previous_read_index = server_reads, reads

    restoration = load(root / ("scenario-restoration-and-final-quiet-state.restoration.json" if scenarios else "final-baseline-restored.restoration.json"))
    assert restoration["ram"] == initial_ram and restoration["coils"] == initial_coils
    assert [ram[0x1000 + index] for index in range(4)] == initial_ram and list(coils.values()) == initial_coils
    if journeys:
        assert restoration["presetRam"] == initial_scratch
        assert [ram[0x1010 + index] for index in range(10)] == initial_scratch
        assert preset_writes == [
            (5, 0x1000, [1]), (5, 0x1000, [0]), (5, 0x1000, [1]), (5, 0x1000, [0]),
            (6, 0x1010, [123]), (6, 0x1011, [0xFF85]), (6, 0x1012, [253]),
            (16, 0x1014, [0x1234, 0x5678]), (16, 0x1016, [0x3FC0, 0]),
            (16, 0x1018, [0xF8A4, 0x32EB]), (6, 0x1010, [6]), (6, 0x1010, [7]), (6, 0x1010, [15])]
        # Require the actual device result read after EVERY write, before the
        # next write. A final post-command read alone cannot prove a step was
        # verified. LED status uses FC04/FC02, independently of its FC01 coil.
        # Each row is: command write/verification pairs, independent final read.
        expected_commands = [
            ("01051000FF0088FA 01041007000184CB", "010110000001F90A"),
            ("010510000000C90A 010210010001ECCA", "010110000001F90A"),
            ("01051000FF0088FA 01041007000184CB 010510000000C90A 010210010001ECCA", "010110000001F90A"),
            ("01061010007BCCEC 010310100001810F", "010310100001810F"),
            ("01061011FF855D5C 010310110001D0CF", "010310110001D0CF"),
            ("0106101200FDEC8E 01031012000120CF", "01031012000120CF"),
            ("011010140002041234567845A4 01031014000280CF", "01031014000280CF"),
            ("011010160002043FC00000B361 010310160002210F", "010310160002210F"),
            ("01101018000204F8A432EB1B69 01031018000240CC", "01031018000240CC"),
            ("0106101000060CCD 010310100001810F 010610100007CD0D 010310100001810F 01061010000FCCCB 010310100001810F", "010310100001810F")]
        expected_requests = []
        for command, post_read in expected_commands:
            expected_requests.extend(command.split() + [post_read, "010410020002D4CB"])
        assert preset_requests == expected_requests, "Preset write/result-read/post-read/counter order differs"
        profile = load(root / "scenario-preset-profile-save-reload-no-transmit.profile-reload.json")
        assert profile["commands"] == 10 and profile["selectedCommand"] == "nucleo-f103rb-ram-sequence" and profile["transmittedBytes"] == 0
        decoded = load(root / "scenario-installed-presets-actual-execution.fc06-value.json")
        assert decoded["hex"] == "01061010000FCCCB" and decoded["Kind"] == "RequestOrResponse" and decoded["ValuesText"] == "+0: 15"
    assert not restoration["resetIssued"]
    assert writes == expected_writes == ((last_counter - initial_counter) & 0xFFFFFFFF)
    assert exceptions == 1 and corrupt_acks == (0 if scenarios else 1)
    result = dict(passed=True, mode=mode, cases=expected_cases, rtu_reads=reads, valid_writes=writes,
                  known_exceptions=exceptions, expected_corrupt_acks=corrupt_acks, text_requests=text_requests,
                  initial_write_counter=initial_counter, final_write_counter=last_counter,
                  baseline_ram_restored=True, baseline_coils_restored=True, reset_issued=False)
    if journeys:
        result.update(preset_commands=10, preset_write_steps=13, baseline_preset_ram_restored=True,
                      profile_reload_without_tx=True, fc06_value_interpretation=True,
                      preset_verification_order=True)
    with (root / "independent-audit.json").open("x", encoding="utf-8") as output:
        json.dump(result, output, indent=2)
    with (root / "sha256.txt").open("x", encoding="utf-8") as output:
        for path in sorted(root.iterdir()):
            if path.is_file() and path.name != "sha256.txt":
                output.write(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n")
    print(json.dumps(result))


if __name__ == "__main__":
    audit(Path(sys.argv[1]).resolve())
