# Contributing to BatteryDesk

## Report a device

Try the [current preview](https://github.com/CharlieMinh/BatteryDesk/releases) on Windows 11 x64. Open **Thiết bị** in the battery panel, select your device, then use **Cài đặt → Sao chép thông tin hỗ trợ…**. Review the text before copying it to the [device compatibility form](https://github.com/CharlieMinh/BatteryDesk/issues/new/choose).

Please include the exact model, the connection you tested, and what happened. For a charging or reconnect issue, write down the steps and whether the value came from a fresh read or the last known reading. If you tested more than one connection, report each separately. Omit serial numbers and Bluetooth addresses.

## Build and test

Install the .NET 10 SDK on Windows, then run:

```powershell
dotnet build BatteryDesk.slnx -c Release
dotnet test BatteryDesk.slnx -c Release
```

`BatteryDesk.Core` owns device discovery, protocol readers, and battery state. `BatteryDesk` is the WinForms app. `BatteryDesk.Probe` is the command-line tool for local comparisons. The automated tests run with sample data, so support for a new model also needs a hardware report.

For new USB support, scope reads to the relevant VID/PID and HID collection. Document the source of the protocol fields and how they were verified. Add a parser test for a valid response and for any response the reader should reject. Update the [compatibility matrix](docs/COMPATIBILITY.md) with the specific connection tested.
