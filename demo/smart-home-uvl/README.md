# Smart home (UVL)

Three scopes, each in its own UVL file, linked only by capabilities:

| Scope | File | Requires | Provides |
|---|---|---|---|
| `home.lighting` | `lighting.uvl` | `WirelessMesh` (SmartBulbs), `MotionSensing` (MotionLights) | `WirelessMesh` (BluetoothMesh) |
| `home.security` | `security.uvl` | `HighBandwidth` (Cameras), `WirelessMesh` (DoorSensors), `CloudAccess` (CloudBackup) | `VideoStream`, `MotionSensing`, `OfflineMode`, `EncryptedVideo` |
| `home.network` | `network.uvl` | nothing | `WirelessMesh`, `HighBandwidth` (WifiMesh), `CloudAccess` (CloudBridge) |

Global rules (`rules.yaml`):

- `CloudAccess ≠ OfflineMode`: a cloud bridge and local-only mode cannot coexist.
- `VideoStream ⇒ EncryptedVideo`: recorded video must be encrypted.
- `WirelessMesh ≤ 1 provider`: only one mesh at a time (the network's radio or the bulbs' own Bluetooth mesh).

Scenarios in `configs/` (`load <name>` in the explorer):

| Scenario | Shows |
|---|---|
| `valid-home` | everything satisfied |
| `no-bandwidth` | Cameras need `HighBandwidth`, Zigbee does not provide it: only `home.network` is told |
| `unencrypted-video` | global rule `VideoStream ⇒ EncryptedVideo` violated |
| `two-meshes` | global rule `WirelessMesh ≤ 1 provider` violated (Bluetooth mesh and Zigbee) |
| `cloud-vs-local` | global rule `CloudAccess ≠ OfflineMode` violated |
| `no-motion-source` | Motion Lights need `MotionSensing`, but Security picked nothing |

Run it:

```bash
dotnet run --project demo/CapabilityResolver.Demo -- explore demo/smart-home-uvl
```
