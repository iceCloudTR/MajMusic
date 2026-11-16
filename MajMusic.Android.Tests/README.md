# Android native credential regression

This independent Android app links the production credential store and uses the package `com.icecloud.majmusic.tests`. Its separate Android UID owns the test preferences and Keystore alias. Tests generate random values, verify persistence across store instances and clean up their own logical keys. Logcat contains only safe PASS/FAIL markers, operation names and exception types.

Run against an explicitly selected, authorized device:

```powershell
./Tools/Test-AndroidCredentials.ps1 -Serial <adb-device-serial>
```

The script builds and installs only the test app, records results under `artifacts`, and uninstalls the test app afterward. `-KeepInstalled` retains it for inspection. The actual MajMusic application is not changed.

To compare an older implementation, save that source in an ignored directory and pass `-CredentialStoreSource <absolute-path>`. The MSBuild property of the same name defaults to `MajMusic.Android/Security/AndroidCredentialStore.cs`.
