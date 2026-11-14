"""Check the independent auditor against altered copies, with no device access.

Usage: python audit-negative.py FRESH_V2_EVIDENCE NEW_OUTPUT_DIRECTORY
The input evidence and its existing audit files are never modified.
"""
import json
import shutil
import subprocess
import sys
from pathlib import Path


def main(source, output):
    if output == source or source in output.parents:
        raise ValueError("Output directory must be outside the input evidence directory.")
    output.mkdir()  # Refuse to overwrite any previous experiment.
    auditor = Path(__file__).with_name("audit-live.py")
    lines = [json.loads(line) for line in (source / "wire.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    exchanges = []
    for entry in lines:
        if entry["Direction"] == "TX":
            exchanges.append([])
        exchanges[-1].append(entry)
    target = "scenario-installed-presets-actual-execution"
    write = next(index for index, pair in enumerate(exchanges)
                 if pair[0]["Case"] == target and pair[0]["hex"] == "0106101000060CCD")
    status = next(index for index, pair in enumerate(exchanges)
                  if pair[0]["Case"] == target and pair[0]["hex"] == "01041007000184CB")
    results = []
    for mode in ("unchanged", "response-crc", "missing-response", "duplicate-write", "missing-step-readback", "wrong-status-area"):
        root = output / mode
        shutil.copytree(source, root, ignore=shutil.ignore_patterns("independent-audit.json", "sha256.txt"))
        copied = json.loads(json.dumps(exchanges))
        if mode == "response-crc":
            reply = copied[write][-1]
            data = bytearray.fromhex(reply["hex"])
            data[-1] ^= 1
            reply["hex"] = data.hex().upper()
        elif mode == "missing-response":
            copied[write] = copied[write][:1]
        elif mode == "duplicate-write":
            copied.insert(write + 1, copied[write])
        elif mode == "missing-step-readback":
            del copied[write + 1]
        elif mode == "wrong-status-area":
            # Valid FC01 coil value 1, replacing FC04 status value 1. Both CRCs
            # are correct, so only the required result-check contract rejects it.
            tx = copied[status][0]
            rx = dict(copied[status][-1])
            tx["hex"] = "010110000001F90A"
            rx["hex"] = "010101019048"
            copied[status] = [tx, rx]
        if mode != "unchanged":
            with (root / "wire.jsonl").open("w", encoding="utf-8") as stream:
                for pair in copied:
                    for entry in pair:
                        stream.write(json.dumps(entry) + "\n")
        run = subprocess.run([sys.executable, str(auditor), str(root)], capture_output=True, text=True)
        accepted = run.returncode == 0
        expected = mode == "unchanged"
        results.append(dict(case=mode, accepted=accepted, expected=expected,
                            passed=accepted == expected, stdout=run.stdout, stderr=run.stderr))
    (output / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    for result in results:
        print(f"{result['case']}: accepted={result['accepted']} expected={result['expected']} passed={result['passed']}")
    return 0 if all(result["passed"] for result in results) else 1


if __name__ == "__main__":
    raise SystemExit(main(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()))
