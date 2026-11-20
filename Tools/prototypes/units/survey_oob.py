"""Survey HOI4 1936 land orders of battle (history/units) to plan the unit conversion.

Picks each country's base-game 1936 land OOB (set_oob/oob outside date blocks, non-DLC branch),
then reports divisions, templates, regiment/support types, locations and stacking.
Usage: python survey_oob.py [--hoi4 PATH]
"""
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from hoi4_convert import DEFAULT_HOI4, collect_oob_names, parse_pdx, get, get_all, read_text  # noqa: E402

hoi4 = Path(sys.argv[sys.argv.index("--hoi4") + 1]) if "--hoi4" in sys.argv else Path(DEFAULT_HOI4)


oob_of = {}
for f in sorted((hoi4 / "history" / "countries").glob("*.txt")):
    found = []
    collect_oob_names(parse_pdx(read_text(f)), found)
    base = [o for o, dlc in found if not dlc] or [o for o, _ in found]
    if base:
        oob_of[f.name[:3]] = base[0]

templates_used = Counter()
regiment_types = Counter()
support_types = Counter()
per_country = Counter()
per_province = Counter()
template_sizes = Counter()
keys_seen = Counter()
missing = []
locations = set()
for tag, oob in sorted(oob_of.items()):
    path = hoi4 / "history" / "units" / f"{oob}.txt"
    if not path.exists():
        missing.append(oob)
        continue
    root = parse_pdx(read_text(path))
    templates = {}
    for t in get_all(root, "division_template"):
        name = get(t, "name")
        regs = [k for k, _, _ in (get(t, "regiments") or [])]
        sups = [k for k, _, _ in (get(t, "support") or [])]
        templates[name] = (regs, sups)
    for units in get_all(root, "units"):
        for d in get_all(units, "division"):
            for k, _, _ in d:
                keys_seen[k] += 1
            tname = get(d, "division_template")
            loc = int(get(d, "location", 0))
            per_country[tag] += 1
            per_province[loc] += 1
            locations.add(loc)
            templates_used[(tag, tname)] += 1
            regs, sups = templates.get(tname, ([], []))
            template_sizes[len(regs)] += 1
            regiment_types.update(regs)
            support_types.update(sups)

total = sum(per_country.values())
print(f"countries with a land OOB: {len(oob_of)}, files missing: {missing[:10]}")
print(f"divisions: {total} in {len(per_country)} countries, {len(locations)} distinct provinces")
print("top countries:", per_country.most_common(12))
print("division keys:", keys_seen.most_common())
print("regiment types:", regiment_types.most_common())
print("support types:", support_types.most_common())
print("regiments per division:", sorted(template_sizes.items()))
stack = Counter(per_province.values())
print("divisions per province (stack size: provinces):", sorted(stack.items()))
print("biggest stacks:", per_province.most_common(5))
print("distinct (country, template):", len(templates_used))
