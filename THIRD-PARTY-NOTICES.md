# Protocol and dependency provenance

BatteryDesk's own code is distributed under the [MIT license](LICENSE). The entries below record materials consulted during implementation and components distributed with the portable package.

| Area | Reference and use |
|---|---|
| GATT and Windows HID APIs | [Microsoft GATT client](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client) and [Windows HID collections](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/opening-hid-collections). Windows components are provided by the operating system. |
| Logitech HID++ | [Logitech's HID++ 2.0 documentation](https://github.com/Logitech/cpg-docs/tree/master/hidpp20) and [Solaar's HID++ reader](https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/hidpp20.py) were consulted for report framing, feature identifiers, and battery response fields. The cited Solaar file specifies GPL version 2 or later. BatteryDesk uses its own Windows HID transport and C# request matching. |
| Dareu Togran | [Togran notes in open-ek75](https://github.com/mateusands/open-ek75/blob/main/PROTOCOL.md) and the [DAREU protocol reference](https://github.com/notjiccu/wireless-device-battery-monitor/blob/main/src/drivers/dareu.cpp) were consulted for the battery command and report fields. Both upstream repositories publish GPL 3.0 licenses. BatteryDesk implements a C# feature-report reader for the EK75 PRO cable interface, verified on physical hardware. |
| Fonts and icons | The app uses fonts already installed by Windows. BatteryDesk's rounded device outlines are drawn in its own source. Fonts are not included as app assets. |
| Bundled runtime | The self-contained candidate includes Microsoft .NET and Windows Desktop runtime files. The package includes the license files and .NET Runtime third-party notices from the resolved NuGet runtime packs in `licenses/`. The Windows SDK interop files come from the [Microsoft.Windows.SDK.NET.Ref targeting pack](https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.19041.57), which links to the [Windows SDK license](https://aka.ms/WinSDKLicenseURL). |

The distributed package does not incorporate source files from the referenced projects. Their documentation and source remain under their own licenses.
