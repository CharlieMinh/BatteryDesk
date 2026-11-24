# BatteryDesk 1.0.0-rc.2 compatibility

## Hardware evidence

| Device | Connection | Mechanism | Verification |
|---|---|---|---|
| Dareu EK75 PRO | Bluetooth Low Energy | Standard GATT battery characteristic, with a Windows fallback | Real hardware. Previously read 50% via GATT and Windows. |
| Dareu EK75 PRO | USB cable, 260D:0046 | Togran battery feature report on the 65-byte consumer collection | Real hardware. The keyboard reported 100% with the charging flag while Bluetooth was off. |
| Logitech G304 LIGHTSPEED | USB receiver, 046D:C53F | Logitech HID++ feature 0x1000 on the long vendor reports collection | Real hardware. Read 90% and observed the last-read state while the device did not respond. |
| Other Bluetooth LE peripherals | BLE | GATT when the device makes its battery service available | Generic read path, device-specific behavior to verify. |
| Bluetooth Classic peripherals | Bluetooth Classic | Battery level exposed by the Windows device stack | Reading path implemented, hardware verification pending. |

The candidate targets Windows 11 x64. The operating system build and the precise peripheral connection matter when reporting a result. A model mentioned in another project’s compatibility list is not evidence for BatteryDesk. A device without a confirmed percentage remains at “No data” or shows its last confirmed observation. Earbuds with multiple battery characteristics need separate validation.

On the development machine, the extracted self-contained candidate read the wired EK75 PRO at 100% and the G304 receiver at 90%. A local UI test used the real cable connection and an offline test profile with the same model name to verify that both entries stayed separate until manually linked. The same test verified per-device settings, the support preview and a physical battery sample persisted to local history.

RC2 was verified on the same machine after the EK75 PRO switched to Bluetooth. The taskbar and battery panel showed one keyboard and one mouse with the previous USB profile still saved. The cable-to-Bluetooth and Bluetooth-to-cable transitions also have automated regression coverage.

## Candidate checks still needed on hardware

- EK75 PRO below 100% when switching between Bluetooth and USB. Verify live charge progress and a real charge-target notification.
- Physical receiver unplug/replug and sleep/wake over a longer run.
- Windows 11 x64 clean VM or another clean machine, plus Explorer restart and additional monitor/DPI combinations.
- Bluetooth Classic device that exposes a level to Windows.
