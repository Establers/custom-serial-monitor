namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusRtuViewModel
{
    public string GuideLabel => T("Guide", "Usage guide");
    public string GuideTitle => T("GuideTitle", "Device monitoring & control guide");
    public string GuidePurpose => T("GuidePurpose", "View values that a Modbus device exposes at numbered addresses, such as temperatures, status and settings. Read repeatedly to watch changes, or edit values that the device allows you to write.");
    public string GuideModesTitle => T("GuideModesTitle", "Choose a view");
    public string GuideModesHelp => T("GuideModesHelp", "Device monitoring & control is the default view for requested values and command results. Packet analysis inspects RX/TX packets. Data sent by the device without a request can be inspected in the log or packet analysis. Switching views does not start communication.");
    public string GuideStartTitle => T("GuideStartTitle", "Start with one read");
    public string GuideConnectStep => T("GuideConnectStep", "1. Connect using the device's COM port, baud rate and parity. Have its register map ready.");
    public string GuideSetupStep => T("GuideSetupStep", "2. Set the unit ID, read function, start address and quantity. For example, ID 1 / FC03 / address 0 / quantity 10 reads addresses 0–9. Addresses start at 0: holding register 40001 corresponds to address 0.");
    public string GuideReadStep => T("GuideReadStep", "3. Use Read once to check the values, then Start polling to read repeatedly and update the table. The interval is the minimum wait after a response before the next read. Stop keeps the last values.");
    public string GuideGroupsTitle => T("GuideGroupsTitle", "Read separate areas or multiple devices");
    public string GuideGroupsHelp => T("GuideGroupsHelp", "Polling groups lets you register multiple named unit IDs, read areas and intervals. Start enabled groups polls the enabled groups in turn. Select a group to see its table. Read once and Direct write target the selected group. All groups on the same COM connection share its communication settings.");
    public string GuideDisplayTitle => T("GuideDisplayTitle", "Display type and multiplier");
    public string GuideDisplayHelp => T("GuideDisplayHelp", "The display type interprets the device's raw data as numbers. UInt is an unsigned integer, Int also allows negative integers, and Float32 is a floating-point number. A 16-bit value uses one register; a 32-bit value uses two. A multiplier of 0.1 displays raw 253 as 25.3. Match the type, multiplier and byte/word order to the register map. Defined items use their own display settings.");
    public string GuideWriteTitle => T("GuideWriteTitle", "Change a device value");
    public string GuideWriteHelp => T("GuideWriteHelp", "Select a row, open Direct write, check the address and preview, then choose Write and verify. Raw values are numbers before scaling; Item value uses the defined item's display units. Only FC01 coils and FC03 holding registers are writable. Writing changes the actual device value.");
    public string GuideConnectionTitle => T("GuideConnectionTitle", "Sequences and other senders");
    public string GuideSaveHelp => T("GuideSaveHelp", "Target settings includes item definitions, polling groups and Save profile. Apply to monitor keeps the editor draft in this session; Save profile stores it for later. Loading or reconnecting never starts communication.");
}
