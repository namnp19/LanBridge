#!/usr/bin/env python3
"""XML/source-layout sanity check. Not a C# compiler or Windows UAT replacement."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
required = [
    "src/LanBridge.Core/LanBridge.Core.csproj",
    "src/LanBridge.Core/Models.cs",
    "src/LanBridge.Core/DeviceIdentity.cs",
    "src/LanBridge.Core/GroupLedger.cs",
    "src/LanBridge.Core/WireIO.cs",
    "src/LanBridge.Desktop/LanBridge.Desktop.csproj",
    "src/LanBridge.Desktop/App.xaml",
    "src/LanBridge.Desktop/App.xaml.cs",
    "src/LanBridge.Desktop/MainWindow.xaml",
    "src/LanBridge.Desktop/MainWindow.xaml.cs",
    "src/LanBridge.Desktop/LanBridgeEngine.cs",
    "src/LanBridge.Desktop/Engine.Network.cs",
    "src/LanBridge.Desktop/Engine.Group.cs",
    "src/LanBridge.Desktop/Engine.Transfer.cs",
    "src/LanBridge.Desktop/HotkeyService.cs",
    "src/LanBridge.Desktop/LocalStore.cs",
    "src/LanBridge.Desktop/LanDiscovery.cs",
    "installer/LanBridge.iss",
    "scripts/build.ps1",
    "scripts/firewall.ps1",
    "tests/LanBridge.SmokeTests/Program.cs",
]
for name in required:
    assert (root / name).is_file(), f"Missing {name}"
for xml in list(root.rglob("*.csproj")) + list(root.rglob("*.xaml")):
    ET.parse(xml)
    print("PASS XML", xml.relative_to(root))
for path in root.rglob("*.cs"):
    source = path.read_text(encoding="utf-8")
    assert "namespace " in source or path.name == "Program.cs", f"Missing namespace: {path}"
    assert "TODO STUB" not in source, f"Stub marker: {path}"
    print("PASS SOURCE EXISTS", path.relative_to(root), len(source.splitlines()), "lines")
print("Source structure verified. Compile and Windows test separately.")
