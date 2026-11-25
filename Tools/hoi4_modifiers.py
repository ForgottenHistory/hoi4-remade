"""A country's HOI4 modifiers at the start, summed the way the game's Hoi4Modifiers does on the engine's modifier
system: its ideas and laws (common/hoi4_country_economy.json5 "ideas", common/hoi4_ideas.json5) plus its known
technologies (common/hoi4_technologies.json5). Every key sums its values; consumer_goods_factor stacks
multiplicatively (product of 1 + value).

    from hoi4_modifiers import country_modifiers
    m = country_modifiers("GER")
    m.sum("research_speed_factor"), m.product("consumer_goods_factor")
"""
from pathlib import Path

from manpower_pools import json5

DATA = Path(__file__).resolve().parents[1] / "Hoi4Data" / "common"


def _load(name):
    return json5(DATA / name)


class CountryModifiers:
    def __init__(self, sources):
        self.sources = sources  # [(source name, {key: value})]

    def sum(self, key):
        return sum(m.get(key, 0.0) for _, m in self.sources)

    def product(self, key):
        p = 1.0
        for _, m in self.sources:
            if key in m:
                p *= 1.0 + m[key]
        return p


def country_modifiers(tag):
    economy = _load("hoi4_country_economy.json5")
    ideas = _load("hoi4_ideas.json5")
    tdoc = _load("hoi4_technologies.json5")
    e = economy.get(tag, {})
    sources = [(i, ideas[i]["modifiers"]) for i in e.get("ideas", []) if i in ideas]
    sources += [(t, tdoc["technologies"][t].get("modifiers", {})) for t in tdoc["countries"].get(tag, [])
                if t in tdoc["technologies"]]
    return CountryModifiers(sources)
