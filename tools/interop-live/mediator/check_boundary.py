#!/usr/bin/env python3
"""
Cell B-0 of the mediator leg (E-36): the external-mediator wallet references only
DidComm.Mediator.Client.

MediatorWallet is what a wallet application looks like: it must reach an external mediator
with the client package alone, never through the mediator packages (DidComm.Mediator,
DidComm.Mediator.AspNetCore), DidComm.AspNetCore or ASP.NET Core. This checks five views of
that, so a reference that sneaks in through any route fails the cell:

  1. obj/project.assets.json `libraries`: the restored package and project graph, transitive
     references included;
  2. obj/project.assets.json `frameworkReferences` of every target framework;
  3. bin/.../MediatorWallet.runtimeconfig.json: the shared frameworks it runs on
     (Microsoft.NETCore.App only);
  4. bin/.../MediatorWallet.deps.json: what the host loads;
  5. --refs: the compiled assembly's references as `MediatorWallet boundary` reports them
     (Assembly.GetReferencedAssemblies, a cross-check of the graph above).

Positive control: DidComm.Mediator.Client must be present in views 1, 4 and 5 (a check that
finds nothing proves nothing). --forbid NAME adds a forbidden name; the leg's negative control
forbids DidComm.Mediator.Client itself and requires this to FAIL.

Stdlib only; run with the venv interpreter and -I. Exit 0 when every check passes, 1 otherwise.
"""

import argparse
import glob
import json
import os
import sys

FORBIDDEN_EXACT = {"DidComm.Mediator", "DidComm.Mediator.AspNetCore", "DidComm.AspNetCore"}
FORBIDDEN_PREFIX = ("Microsoft.AspNetCore.",)
REQUIRED_DEFAULT = "DidComm.Mediator.Client"


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def library_name(key: str) -> str:
    return key.split("/", 1)[0]


def referenced_assemblies(refs: dict) -> set:
    """`MediatorWallet boundary`'s referenced_assemblies (names, or "Name, Version=…" display names)."""
    names = refs.get("referenced_assemblies")
    if not isinstance(names, list):
        raise ValueError("the boundary JSON has no referenced_assemblies list")
    return {str(n).split(",", 1)[0].strip() for n in names}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--project-dir", required=True, help="tools/interop-live/mediator/MediatorWallet")
    parser.add_argument("--bin-dir", required=True, help="its bin/Release/<tfm> directory")
    parser.add_argument("--refs", required=True, help="the JSON `MediatorWallet boundary --out` wrote")
    parser.add_argument("--assembly", default="MediatorWallet")
    parser.add_argument("--forbid", action="append", default=[], help="an extra forbidden name (repeatable)")
    parser.add_argument("--require", default=REQUIRED_DEFAULT)
    args = parser.parse_args()

    extra = set(args.forbid)
    failures = []

    def forbidden(name: str) -> bool:
        return name in FORBIDDEN_EXACT or name in extra or name.startswith(FORBIDDEN_PREFIX)

    def check(label, names, require=True):
        bad = sorted(n for n in names if forbidden(n))
        if bad:
            failures.append(f"{label}: forbidden {bad}")
            print(f"FAIL {label}: forbidden {bad}")
        else:
            print(f"PASS {label}: none of the forbidden names among {len(names)}")
        if require:
            if args.require in names:
                print(f"PASS {label}: {args.require} present (positive control)")
            else:
                failures.append(f"{label}: {args.require} absent")
                print(f"FAIL {label}: {args.require} absent (positive control)")

    assets = load(os.path.join(args.project_dir, "obj", "project.assets.json"))
    check("project.assets.json libraries", {library_name(k) for k in assets.get("libraries", {})})

    framework_refs = set()
    for framework in (assets.get("project", {}).get("frameworks") or {}).values():
        framework_refs |= set((framework.get("frameworkReferences") or {}).keys())
    bad = sorted(r for r in framework_refs if r == "Microsoft.AspNetCore.App" or forbidden(r))
    if bad:
        failures.append(f"frameworkReferences: {bad}")
        print(f"FAIL project.assets.json frameworkReferences: {bad}")
    else:
        print(f"PASS project.assets.json frameworkReferences: {sorted(framework_refs) or 'none'}")

    runtime = load(os.path.join(args.bin_dir, f"{args.assembly}.runtimeconfig.json")).get("runtimeOptions", {})
    frameworks = [runtime["framework"]] if isinstance(runtime.get("framework"), dict) else list(runtime.get("frameworks") or [])
    names = sorted(f.get("name") for f in frameworks)
    if names != ["Microsoft.NETCore.App"]:
        failures.append(f"runtimeconfig frameworks {names}")
        print(f"FAIL {args.assembly}.runtimeconfig.json: frameworks {names}, expected only Microsoft.NETCore.App")
    else:
        print(f"PASS {args.assembly}.runtimeconfig.json: frameworks {names}")

    deps = load(os.path.join(args.bin_dir, f"{args.assembly}.deps.json"))
    dep_names = {library_name(k) for k in deps.get("libraries", {})}
    for target in (deps.get("targets") or {}).values():
        dep_names |= {library_name(k) for k in target}
    check(f"{args.assembly}.deps.json", dep_names)

    dlls = {os.path.splitext(os.path.basename(p))[0] for p in glob.glob(os.path.join(args.bin_dir, "*.dll"))}
    check("assemblies in the output directory", dlls)

    check("compiled references (MediatorWallet boundary)", referenced_assemblies(load(args.refs)))

    if failures:
        print(f"FAIL boundary: {len(failures)} check(s) failed: " + "; ".join(failures))
        sys.exit(1)
    print("PASS boundary: the wallet references DidComm.Mediator.Client and nothing of the mediator role or ASP.NET Core")


if __name__ == "__main__":
    main()
