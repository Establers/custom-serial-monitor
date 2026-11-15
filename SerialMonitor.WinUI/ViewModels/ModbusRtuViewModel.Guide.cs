namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusRtuViewModel
{
    public string GuideLabel => T("Guide", "Usage guide");
    public string GuideTitle => T("GuideTitle", "Device monitoring & control guide");
    public string GuidePurpose => T("GuidePurpose", "Read temperatures, status and settings from device addresses into the table. Turn on automatic reading to watch them change.");
    public string GuideModesTitle => T("GuideModesTitle", "Choose a view");
    public string GuideModesHelp => T("GuideModesHelp", "Choose Device monitoring & control to read or change values, or Packet analysis to inspect the original RX/TX packets.");
    public string GuideStartTitle => T("GuideStartTitle", "Read device values");
    public string GuideConnectStep => T("GuideConnectStep", "1. Connect at the top using the device's COM port, baud rate and parity.");
    public string GuideSetupStep => T("GuideSetupStep", "2. Enter the ID, data area, start address and count in Read settings. Example: ID 1 / 03 Holding registers / address 0 / count 10 → addresses 0–9.");
    public string GuideReadStep => T("GuideReadStep", "3. Read current values to check the device. Start automatic reading to keep updating. Stopping leaves the last values in the table.");
    public string GuideGroupsTitle => T("GuideGroupsTitle", "Read separate areas or multiple devices");
    public string GuideGroupsHelp => T("GuideGroupsHelp", "A read group is a named device ID and address range. Add groups under Read groups and include the ones you want to read automatically. Automatic reading visits all included groups; Read selected group reads only the displayed group once.");
    public string GuideDisplayTitle => T("GuideDisplayTitle", "Display type and multiplier");
    public string GuideDisplayHelp => T("GuideDisplayHelp", "The display type interprets the device's raw data as numbers. UInt is an unsigned integer, Int also allows negative integers, and Float32 is a floating-point number. A 16-bit value uses one register; a 32-bit value uses two. A multiplier of 0.1 displays raw 253 as 25.3. Match the type, multiplier and byte/word order to the register map. Defined items use their own display settings.");
    public string GuideWriteTitle => T("GuideWriteTitle", "Change a device value");
    public string GuideWriteHelp => T("GuideWriteHelp", "Select a row → Write values → check the address and value → Write to device and verify. Raw numbers are before scaling. Only 01 Coils and 03 Holding registers support writes.");
    public string GuideConnectionTitle => T("GuideConnectionTitle", "Sequences and other senders");
    public string GuideSaveHelp => T("GuideSaveHelp", "Manage names, units and read groups in Read settings. Apply updates this session; Save settings for next time keeps them for later.");
}
