"""Type-check native C# on Mac using restored Windows reference assemblies.

This does NOT compile XAML, generate XBF, or validate WinUI at runtime. Temporary
named-element declarations replace only XAML-generated fields for this check.
Run a normal native build once to let MSBuild produce XAML compiler input.json.
"""
import json
import os
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
project = root / "src/ImgZip.App"
inputs = list((project / "obj/Release").rglob("input.json"))
if not inputs:
    raise SystemExit("Run native restore/build first to generate XAML input.json (the XAML step requires Windows).")
data = json.loads(inputs[0].read_text())
temporary = root / ".tools/native-csharp-check"
temporary.mkdir(parents=True, exist_ok=True)
xns = "{http://schemas.microsoft.com/winfx/2006/xaml}"
declarations = []
for source in project.glob("*.xaml"):
    tree = ET.parse(source)
    cls = tree.getroot().get(xns + "Class")
    if not cls:
        continue
    namespace, name = cls.rsplit(".", 1)
    declarations += [f"namespace {namespace} {{ partial class {name} {{", "private void InitializeComponent() { }"]
    for element in tree.iter():
        field = element.get(xns + "Name")
        if field:
            tag = element.tag.rsplit("}", 1)[-1]
            controls = "Microsoft.UI.Xaml.Controls.Primitives" if tag == "ToggleButton" else "Microsoft.UI.Xaml.Controls"
            declarations.append(f"private {controls}.{tag} {field} = null!;")
    declarations.append("} }")
(temporary / "XamlNamedElements.cs").write_text("\n".join(declarations))
responses = ["/nologo", "/target:library", "/nullable:enable", "/langversion:14", "/out:\"" + str(temporary / "NativeCodeCheck.dll") + "\""]
for item in data["ReferenceAssemblies"]:
    if Path(item["FullPath"]).suffix.lower() == ".dll":
        responses.append('/reference:"' + item["FullPath"] + '"')
for source in list(project.glob("*.cs")) + [temporary / "XamlNamedElements.cs"] + list((project / "obj/Release").rglob("*GlobalUsings.g.cs"))[:1]:
    responses.append('"' + str(source) + '"')
response = temporary / "check.rsp"
response.write_text("\n".join(responses))
sdk = json.loads((root / "global.json").read_text())["sdk"]["version"]
environment = dict(os.environ, DOTNET_CLI_HOME=str(root / ".tools/dotnet-home"), DOTNET_NOLOGO="1")
subprocess.run([str(root / ".tools/dotnet/dotnet"), str(root / f".tools/dotnet/sdk/{sdk}/Roslyn/bincore/csc.dll"), "@" + str(response)], check=True, env=environment)
print("Native C# type-check passed. XAML compilation and Windows execution remain unverified.")
