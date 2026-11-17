# MajView

MajView is developed by TeamMajdata. The renderer source is distributed under the GNU General Public License version 3. This package's [runtime manifest](MajView.Runtime.json) identifies its renderer revision and file checksums. See [the license](MajView.LICENSE).

- [MajdataNet WebGL release files at e9db7b4](https://github.com/TeamMajdata/MajdataNet/tree/e9db7b4b431954f2e9fefa7745d3650b93fc4671/public/WebGLBuild)
- [MajdataView_web renderer source at 7b7addc](https://github.com/TeamMajdata/MajdataView_web/tree/7b7addc468f41786e2ec8672406238befeb8f86d)

Packages with the embedded viewport build also include `MajViewSource/MajViewEmbedded.patch` and `MajViewSource/Build-MajViewRuntime.ps1`. Run the script with `-UnityEditor <path to Unity.exe>` using Unity 6000.3.17f1 and WebGL Build Support to build the patched renderer from the source revision above.
