"""Compile the game layer (Assembly-CSharp) outside Unity, to catch C# errors without switching to the editor.

    python Tools/check_build.py              # the game layer
    python Tools/check_build.py MapAssembly  # one engine project (Core, MapAssembly, StarterKit...), its own .cs files

Builds a temporary copy of the Unity-generated Assembly-CSharp.csproj with `dotnet build`: the engine projects are
referenced as the DLLs Unity last compiled (Library/ScriptAssemblies), plus the packages the engine projects normally
bring in, and every .cs file under Assets/Game is compiled (new files too, before Unity regenerates the csproj).
So engine changes need a Unity compile first, or a check of the engine project they are in (the argument). Prints the errors and exits 1 when there are any.
Needs the .NET SDK and the editor to have been opened once (csproj and Library/).
"""
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def main():
    project = sys.argv[1] if len(sys.argv) > 1 else "Assembly-CSharp"
    game = project == "Assembly-CSharp"
    csproj = ROOT / f"{project}.csproj"
    asm = ROOT / "Library" / "ScriptAssemblies"
    if not csproj.exists() or not asm.exists():
        sys.exit(f"{project}.csproj or Library/ScriptAssemblies missing: open the project in Unity once")
    s = csproj.read_text(encoding="utf-8-sig")

    def ref(name, path):
        return f'<Reference Include="{name}"><HintPath>{path}</HintPath></Reference>'

    s = re.sub(r'<ProjectReference Include="([^"]+)\.csproj"\s*/>', lambda m: ref(m.group(1), asm / (m.group(1) + ".dll")), s)
    s = re.sub(r'<ProjectReference Include="([^"]+)\.csproj">.*?</ProjectReference>',
               lambda m: ref(m.group(1), asm / (m.group(1) + ".dll")), s, flags=re.S)
    # Every game script, listed or not (Unity lists a new file only after it regenerates the csproj)
    if game:
        s = re.sub(r'<Compile Include="Assets\\Game\\[^"]*"\s*/>', "", s)
    s = s.replace('<Compile Include="Assets', f'<Compile Include="{ROOT}\\Assets')
    s = s.replace('<HintPath>Library\\', f'<HintPath>{ROOT}\\Library\\')  # relative to the csproj, which moves
    extra = "".join(f'<Compile Include="{p}" />' for p in sorted((ROOT / "Assets" / "Game").rglob("*.cs"))) if game else ""
    newtonsoft = next(iter(sorted((ROOT / "Library" / "PackageCache").glob("com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll"))), None)
    for name in ("Unity.Collections", "Unity.Mathematics", "Unity.Burst"):
        if (asm / f"{name}.dll").exists():
            extra += ref(name, asm / f"{name}.dll")
    if newtonsoft:
        extra += ref("Newtonsoft.Json", newtonsoft)
    s = s.replace("<ItemGroup>", "<ItemGroup>" + extra, 1)

    work = Path(tempfile.mkdtemp(prefix="hoi4_check_build_"))
    try:
        (work / "Check.csproj").write_text(s, encoding="utf-8")
        r = subprocess.run(["dotnet", "build", str(work / "Check.csproj"), "-nologo", "-v:q",
                            f"-p:OutputPath={work / 'bin'}{chr(92)}", f"-p:BaseIntermediateOutputPath={work / 'obj'}{chr(92)}"],
                           capture_output=True, text=True)
        errors = sorted({re.sub(r"\s*\[[^\]]*Check\.csproj\]$", "", l.strip()) for l in r.stdout.splitlines() if ": error " in l})
        for e in errors:
            print(e)
        print(f"{len(errors)} error(s)")
        sys.exit(1 if errors or r.returncode else 0)
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
