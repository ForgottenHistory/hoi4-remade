"""HOI4 focus trees (common/national_focus) as Lua, for the game's script engine. Used by Tools/hoi4_convert.py.

    python Tools/hoi4_focus.py generic [--hoi4 "<HOI4 install>"] [--out DIR] [--stats]

One Lua file per HOI4 file. A file is a list of calls the game provides: focus_tree { ... } opens a tree and every
focus { ... } after it belongs to it; shared_focus { ... } and joint_focus { ... } stand alone. A focus's fields are
its data (id, icon, x, y, cost, prerequisites...) and its script, as functions of the country `c`:

    available, bypass, allow_branch, cancel   triggers: return whether it holds
    reward, on_select, on_bypass              effects
    shown_reward                              effects the tooltip lists in place of the reward's
    ai                                        the AI's weight (a number, or a function returning one)

How Paradox script becomes Lua:
- A trigger or effect is a method of the scope it is in: has_government = fascism -> c:has_government("fascism"),
  add_political_power = 120 -> c:add_political_power(120). `no` is passed as false (c:has_war(false)), a comparison
  as the operator and the value (c:num_of_factories(">", 50)), a block as a table.
- Scopes are objects: country("SOV"), state(64), s:owner(), c:capital(). ROOT is `c`.
- AND / OR / NOT in the functions shown to the player (available, bypass...) are the calls all(...), any(...),
  none(...): every line is evaluated, so the tooltip can tick or cross each one. Inside effects (an `if`'s limit,
  an iterator's filter) they are Lua's own `and`, `or`, `not`.
- any_/all_ iterators take a function: c:any_owned_state(function(s) return s:is_coastal() end).
- every_ iterators are loops: for _, s in ipairs(c:owned_states()) do ... end.
- random_ iterators return one scope or nil: local s = c:random_owned_controlled_state(function(s) ... end).
- if / else_if / else are Lua's.
Names are HOI4's, but for a few made clearer (RENAME_TRIGGER, RENAME_EFFECT). The translator does not know which
triggers and effects the game implements: it translates everything by shape, and the game reports the methods it
lacks when a script calls them.
"""
import argparse
import re
import sys
from collections import Counter
from pathlib import Path

from hoi4_convert import parse_pdx, read_text, get

DEFAULT_HOI4 = Path(r"C:\Program Files (x86)\Steam\steamapps\common\Hearts of Iron IV")
WIDTH = 110
NUM_RE = re.compile(r"^-?(\d+\.?\d*|\.\d+)$")
TAG_RE = re.compile(r"^[A-Z][A-Z0-9]{2}$")
IDENT_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
LUA_KEYWORDS = {"and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
                "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while"}
OPS = ("<", ">", "<=", ">=", "!=")

BUILDINGS = {"industrial_complex", "arms_factory", "dockyard", "infrastructure", "air_base", "naval_base", "bunker",
             "coastal_bunker", "anti_air_building", "radar_station", "synthetic_refinery", "fuel_silo", "rocket_site",
             "nuclear_reactor", "supply_node", "rail_way"}
# scope changes that are not a tag, a state id or an iterator: key -> (method on the current scope, kind)
SCOPES = {"owner": ("owner", "country"), "controller": ("controller", "country"), "capital_scope": ("capital", "state"),
          "overlord": ("overlord", "country"), "faction_leader": ("faction_leader", "country")}
# triggers and effects that are not about a scope: plain function calls
GLOBAL_TRIGGERS = {"has_dlc", "has_global_flag", "has_game_rule", "is_historical_focus_on", "has_start_date",
                   "country_exists", "date", "difficulty", "is_debug", "is_tutorial"}
GLOBAL_EFFECTS = {"set_global_flag", "clr_global_flag"}
GLOBAL_ITERATORS = {"country", "state", "possible_country"}     # every_country, any_state...
RENAME_TRIGGER = {"tag": "is_tag", "original_tag": "is_original_tag", "impassable": "is_impassable",
                  "has_country_flag": "has_flag", "has_state_flag": "has_flag", "state": "is_state"}
RENAME_EFFECT = {"army_experience": "add_army_experience", "navy_experience": "add_navy_experience",
                 "air_experience": "add_air_experience", "set_country_flag": "set_flag", "clr_country_flag": "clear_flag",
                 "set_state_flag": "set_flag", "clr_state_flag": "clear_flag", "country_event": "fire_event",
                 "add_extra_state_shared_building_slots": "add_building_slots", "clr_global_flag": "clear_global_flag"}
# focus fields copied as they are: HOI4 name -> ours
FOCUS_VALUES = {"icon": "icon", "x": "x", "y": "y", "cost": "cost", "relative_position_id": "relative_to",
                "available_if_capitulated": "available_if_capitulated", "cancel_if_invalid": "cancel_if_invalid",
                "continue_if_invalid": "continue_if_invalid", "bypass_if_unavailable": "bypass_if_unavailable",
                "cancelable": "cancelable", "dynamic": "dynamic", "text": "text", "text_icon": "text_icon"}
FOCUS_TRIGGERS = {"available": "available", "bypass": "bypass", "allow_branch": "allow_branch", "cancel": "cancel"}
FOCUS_EFFECTS = {"completion_reward": "reward", "select_effect": "on_select", "bypass_effect": "on_bypass",
                 "complete_tooltip": "shown_reward"}


# ---- values ---------------------------------------------------------------------------------------------------

def quote(s):
    return '"' + str(s).replace("\\", "\\\\").replace('"', '\\"') + '"'


def key_name(k):
    return k if IDENT_RE.match(k) and k not in LUA_KEYWORDS else f"[{quote(k)}]"


def method(recv, name):
    """recv:name, or recv["name"] when the name is not a Lua identifier (a variable, a scripted token)."""
    if IDENT_RE.match(name) and name not in LUA_KEYWORDS:
        return f"{recv}:{name}" if recv else name
    return None


class Ctx:
    """The scopes of the script being translated: (Lua expression, kind), the root first."""

    def __init__(self, stats):
        self.stack = [("c", "country")]
        self.stats = stats
        self.depth = 0     # nested functions, for parameter names that don't shadow

    @property
    def recv(self):
        return self.stack[-1][0]

    @property
    def kind(self):
        return self.stack[-1][1]

    def scope_word(self, word):
        w = word.upper()
        if w == "ROOT":
            return self.stack[0][0]
        if w == "THIS":
            return self.recv
        if w == "PREV" and len(self.stack) > 1:
            return self.stack[-2][0]
        return None

    def fresh(self, kind):
        base = {"country": "c", "state": "s"}.get(kind, "x")
        used = {e for e, _ in self.stack}
        n = 2
        name = base if base not in used else f"{base}{n}"
        while name in used:
            n += 1
            name = f"{base}{n}"
        return name


def lit(v, ctx):
    """A scalar as a Lua value: number, boolean, scope (ROOT, THIS, PREV) or string."""
    if v == "yes":
        return "true"
    if v == "no":
        return "false"
    if NUM_RE.match(v):
        return v if not v.startswith(".") else "0" + v
    return ctx.scope_word(v) or quote(v)


def table(items, ctx):
    """A block as a Lua table: keys as fields (a repeated key a list), bare values as the array part, a comparison
    as { "<", value }."""
    fields, order, array = {}, [], []
    for k, op, v in items:
        if isinstance(v, tuple):
            continue
        val = table(v, ctx) if isinstance(v, list) else lit(v, ctx)
        if k is None:
            array.append(val)
            continue
        if op in OPS:
            val = f"{{ {quote(op)}, {val} }}"
        if k not in fields:
            order.append(k)
        fields.setdefault(k, []).append(val)
    parts = array + [f"{key_name(k)} = " + (fields[k][0] if len(fields[k]) == 1 else "{ " + ", ".join(fields[k]) + " }")
                     for k in order]
    return "{ " + ", ".join(parts) + " }" if parts else "{}"


def strings(items):
    return [v for k, _, v in items if k is None and isinstance(v, str)]


# ---- expressions (triggers): a small tree, printed on one line when it fits ---------------------------------------

class Raw:
    def __init__(self, text):
        self.text = text


class Call:
    def __init__(self, head, args):
        self.head, self.args = head, args


class Op:
    def __init__(self, op, args):
        self.op, self.args = op, args


class Not:
    def __init__(self, arg):
        self.arg = arg


class Fn:
    def __init__(self, param, body):
        self.param, self.body = param, body


def one_line(n, nested=False):
    if isinstance(n, Raw):
        return n.text
    if isinstance(n, Call):
        return f"{n.head}({', '.join(one_line(a) for a in n.args)})"
    if isinstance(n, Op):
        s = f" {n.op} ".join(one_line(a, True) for a in n.args)
        return f"({s})" if nested else s
    if isinstance(n, Not):
        return "not " + one_line(n.arg, True)
    return f"function({n.param}) return {one_line(n.body)} end"


def fmt(n, ind, used=0, nested=False):
    """The expression as text, broken over lines (indented from `ind`) when it passes WIDTH. `used` is the room
    the text before it takes on its first line."""
    s = one_line(n, nested)
    if len(ind) + used + len(s) <= WIDTH or isinstance(n, Raw):
        return s
    inner = ind + "  "
    if isinstance(n, Call):
        if n.args and isinstance(n.args[-1], Fn):      # a function as the last argument stays on the call's line
            f = n.args[-1]
            first = f"{n.head}({''.join(one_line(a) + ', ' for a in n.args[:-1])}function({f.param})"
            if len(ind) + used + len(first) <= WIDTH:
                return f"{first}\n{inner}return {fmt(f.body, inner, 7)}\n{ind}end)"
        return f"{n.head}(\n" + ",\n".join(inner + fmt(a, inner) for a in n.args) + f"\n{ind})"
    if isinstance(n, Op):
        lines = [fmt(n.args[0], inner, 0, True)] + [f"{inner}{n.op} " + fmt(a, inner, len(n.op) + 1, True) for a in n.args[1:]]
        s = "\n".join(lines)
        return f"(\n{inner}{s}\n{ind})" if nested else s
    if isinstance(n, Not):
        return "not " + fmt(n.arg, ind, used + 4, True)
    return f"function({n.param})\n{inner}return {fmt(n.body, inner, 7)}\n{ind}end"


def combine(how, nodes, shown):
    """AND / OR / NOT (HOI4's NOT is "none of") of trigger nodes."""
    if shown:
        if how == "all" and len(nodes) == 1:
            return nodes[0]
        return Call(how, nodes) if nodes else Raw("true" if how != "any" else "false")
    if not nodes:
        return Raw("true" if how != "any" else "false")
    if how == "none":
        return Not(nodes[0] if len(nodes) == 1 else Op("or", nodes))
    return nodes[0] if len(nodes) == 1 else Op("and" if how == "all" else "or", nodes)


def iterator_kind(name):
    return "state" if "state" in name else "country" if "country" in name else "other"


def plural(name):
    return name[:-1] + "ies" if name.endswith("y") else name + "s"


def scope_of(key, ctx):
    """The scope a key switches to: (Lua expression, kind), or None when the key is not a scope."""
    kl = key.lower()
    word = ctx.scope_word(key) if key.upper() in ("ROOT", "THIS", "PREV") else None
    if word:
        kind = ctx.stack[0][1] if key.upper() == "ROOT" else ctx.kind if key.upper() == "THIS" else ctx.stack[-2][1]
        return word, kind
    if key.isdigit():
        return f"state({key})", "state"
    if TAG_RE.match(key) and kl not in ("and", "not"):
        return f"country({quote(key)})", "country"
    if kl in SCOPES:
        return f"{ctx.recv}:{SCOPES[kl][0]}()", SCOPES[kl][1]
    return None


def trig_list(items, ctx, shown):
    out, i = [], 0
    while i < len(items):
        k, op, v = items[i]
        i += 1
        if k is None or isinstance(v, tuple):
            continue
        kl = k.lower()
        if kl == "if" and isinstance(v, list):
            # if (limit) then the rest [else ...]: when(limit, then, otherwise)
            args = [combine("all", trig_list(get(v, "limit", []), ctx, shown), shown),
                    combine("all", trig_list([t for t in v if t[0] not in ("limit", "else", "else_if")], ctx, shown), shown)]
            other = get(v, "else")
            if other is None and i < len(items) and (items[i][0] or "").lower() == "else":
                other = items[i][2]
                i += 1
            if isinstance(other, list):
                args.append(combine("all", trig_list(other, ctx, shown), shown))
            ctx.stats["trigger"]["when"] += 1
            out.append(Call("when", args))
            continue
        out.append(trig(k, op, v, ctx, shown))
    return out


def trig(k, op, v, ctx, shown):
    kl = k.lower()
    block = isinstance(v, list)
    stats = ctx.stats["trigger"]
    if block and kl in ("and", "or", "not"):
        return combine({"and": "all", "or": "any", "not": "none"}[kl], trig_list(v, ctx, shown), shown)
    if block and kl == "hidden_trigger":
        return Call("hidden", [combine("all", trig_list(v, ctx, shown), shown)])
    if block and kl == "custom_trigger_tooltip":
        rest = [t for t in v if t[0] != "tooltip"]
        return Call("tooltip", [Raw(quote(get(v, "tooltip", ""))), combine("all", trig_list(rest, ctx, shown), shown)])
    if block and kl == "count_triggers":
        rest = [t for t in v if t[0] != "amount"]
        return Call("count_at_least", [Raw(lit(get(v, "amount", "1"), ctx))] + trig_list(rest, ctx, shown))
    if kl == "always" and not block:
        return Raw("true" if v == "yes" else "false")
    if block and (kl.startswith("any_") or kl.startswith("all_")):
        what = kl[4:]
        kind = iterator_kind(what)
        param = ctx.fresh(kind)
        head = kl if what in GLOBAL_ITERATORS else f"{ctx.recv}:{kl}"
        ctx.stack.append((param, kind))
        body = combine("all", trig_list(v, ctx, shown), shown)
        ctx.stack.pop()
        stats[kl] += 1
        return Call(head, [Fn(param, body)])
    scope = scope_of(k, ctx) if block else None
    if scope:
        ctx.stack.append(scope)
        body = combine("all", trig_list(v, ctx, shown), shown)
        ctx.stack.pop()
        return body
    recv = ctx.recv
    if ctx.kind == "state" and kl in BUILDINGS and not block:
        stats["building_level"] += 1
        return Call(f"{recv}:building_level", [Raw(quote(kl)), Raw(quote(op)), Raw(lit(v, ctx))])
    if block and kl == "free_building_slots":
        size = next(((o, x) for key, o, x in v if key == "size"), ("=", "0"))
        args = [quote(get(v, "building", "")), quote(size[0]), lit(size[1], ctx)]
        if get(v, "include_locked") == "yes":
            args.append("true")
        stats[kl] += 1
        return Call(f"{recv}:free_building_slots", [Raw(a) for a in args])
    if block and kl == "has_game_rule":
        stats[kl] += 1
        return Call("has_game_rule", [Raw(quote(get(v, "rule", ""))), Raw(quote(get(v, "option", "")))])
    name = RENAME_TRIGGER.get(kl, kl)
    head = method(None if kl in GLOBAL_TRIGGERS else recv, name)
    stats[name] += 1
    if head is None:      # a variable or token compared with a value
        return Call(f"{recv}:check_variable", [Raw(quote(k)), Raw(quote(op)), Raw(table(v, ctx) if block else lit(v, ctx))])
    if block:
        return Call(head, [Raw(table(v, ctx))])
    if op in OPS:
        return Call(head, [Raw(quote(op)), Raw(lit(v, ctx))])
    if v == "yes":
        return Call(head, [])
    return Call(head, [Raw(lit(v, ctx))])


# ---- statements (effects) -------------------------------------------------------------------------------------------

def condition(items, ctx, ind, used):
    return fmt(combine("all", trig_list(items or [], ctx, False), False), ind, used)


def effects(items, ctx, ind):
    """Lua lines of an effect block."""
    out, i = [], 0
    while i < len(items):
        k, op, v = items[i]
        i += 1
        if k is None or isinstance(v, tuple):
            continue
        kl = k.lower()
        if kl == "if" and isinstance(v, list):
            branch, word = v, "if"
            while True:
                out.append(f"{ind}{word} {condition(get(branch, 'limit', []), ctx, ind, len(word) + 6)} then")
                out += effects([t for t in branch if t[0] not in ("limit", "else", "else_if")], ctx, ind + "  ")
                nested = next(((key, b) for key, _, b in branch if key in ("else_if", "else") and isinstance(b, list)), None)
                if nested:      # the old form: else inside the if
                    key, branch = nested
                elif i < len(items) and (items[i][0] or "").lower() in ("else_if", "else") and isinstance(items[i][2], list):
                    key, branch = items[i][0].lower(), items[i][2]
                    i += 1
                else:
                    break
                if key == "else":
                    out.append(f"{ind}else")
                    out += effects(branch, ctx, ind + "  ")
                    break
                word = "elseif"
            out.append(f"{ind}end")
            continue
        out += effect(k, op, v, ctx, ind)
    return out


def scoped(expr, kind, body_items, ctx, ind, name=None):
    """Effects in another scope. One statement uses the scope's expression; more get a local."""
    limit = get(body_items, "limit")
    body_items = [t for t in body_items if t[0] != "limit"]

    def body(recv, at):
        ctx.stack.append((recv, kind))
        if limit is not None:
            lines = [f"{at}if {condition(limit, ctx, at, 8)} then"] + effects(body_items, ctx, at + "  ") + [f"{at}end"]
        else:
            lines = effects(body_items, ctx, at)
        ctx.stack.pop()
        return lines

    direct = body(expr, ind)
    if len(direct) <= 1 or IDENT_RE.match(expr):
        return direct
    var = name or ctx.fresh(kind)
    return [f"{ind}do", f"{ind}  local {var} = {expr}"] + body(var, ind + "  ") + [f"{ind}end"]


def effect(k, op, v, ctx, ind):
    kl = k.lower()
    block = isinstance(v, list)
    recv = ctx.recv
    stats = ctx.stats["effect"]
    inner = ind + "  "
    if kl == "log":
        return []
    if block and kl in ("hidden_effect", "effect_tooltip"):
        # hidden: done but not listed in the tooltip. tooltip_only: listed but not done.
        name = "hidden" if kl == "hidden_effect" else "tooltip_only"
        return [f"{ind}{name}(function()"] + effects(v, ctx, inner) + [f"{ind}end)"]
    if kl == "custom_effect_tooltip" and not block:
        return [f"{ind}tooltip({quote(v)})"]
    if block and kl == "random":
        rest = [t for t in v if t[0] != "chance"]
        return [f"{ind}if random_chance({lit(get(v, 'chance', '0'), ctx)}) then"] + effects(rest, ctx, inner) + [f"{ind}end"]
    if block and kl.startswith("every_"):
        what = kl[6:]
        kind = iterator_kind(what)
        var = ctx.fresh(kind)
        source = f"{plural(what)}()" if what in GLOBAL_ITERATORS else f"{recv}:{plural(what)}()"
        stats[plural(what)] += 1
        limit = get(v, "limit")
        rest = [t for t in v if t[0] != "limit"]
        ctx.stack.append((var, kind))
        lines = [f"{ind}for _, {var} in ipairs({source}) do"]
        if limit is not None:
            lines += [f"{inner}if {condition(limit, ctx, inner, 8)} then"] + effects(rest, ctx, inner + "  ") + [f"{inner}end"]
        else:
            lines += effects(rest, ctx, inner)
        ctx.stack.pop()
        return lines + [f"{ind}end"]
    if block and kl.startswith("random_") and kl not in ("random_list",):
        what = kl[7:]
        kind = iterator_kind(what)
        var = ctx.fresh(kind)
        head = kl if what in GLOBAL_ITERATORS else f"{recv}:{kl}"
        stats[kl] += 1
        limit = get(v, "limit")
        prioritize = get(v, "prioritize")
        rest = [t for t in v if t[0] not in ("limit", "prioritize")]
        ctx.stack.append((var, kind))
        args = []
        if limit is not None or prioritize is not None:
            first = f"{ind}local {var} = {head}("
            args.append(fmt(Fn(var, combine("all", trig_list(limit or [], ctx, False), False)), ind, len(first)) if limit is not None else "nil")
        if isinstance(prioritize, list):
            args.append(f"{{ prioritize = {table(prioritize, ctx)} }}")
        body = effects(rest, ctx, inner)
        ctx.stack.pop()
        return [f"{ind}local {var} = {head}({', '.join(args)})", f"{ind}if {var} then"] + body + [f"{ind}end"]
    scope = scope_of(k, ctx) if block else None
    if scope:
        name = k.lower() if TAG_RE.match(k) else None
        if name in LUA_KEYWORDS:      # FOR (Formosa)
            name += "_"
        return scoped(scope[0], scope[1], v, ctx, ind, name)

    # ---- effects with a clearer shape than their block
    if kl in ("add_ideas", "remove_ideas"):
        name = "add_idea" if kl == "add_ideas" else "remove_idea"
        stats[name] += 1
        return [f"{ind}{recv}:{name}({quote(idea)})" for idea in (strings(v) if block else [v])]
    if block and kl == "swap_ideas":
        stats["swap_idea"] += 1
        return [f"{ind}{recv}:swap_idea({quote(get(v, 'remove_idea', ''))}, {quote(get(v, 'add_idea', ''))})"]
    if block and kl == "add_timed_idea":
        stats["add_idea"] += 1
        rest = [t for t in v if t[0] != "idea"]
        return [f"{ind}{recv}:add_idea({quote(get(v, 'idea', ''))}, {table(rest, ctx)})"]
    if block and kl == "add_tech_bonus":
        stats[kl] += 1
        parts = [f"{key_name(key)} = {lit(x, ctx)}" for key, _, x in v if key not in ("category", "technology") and isinstance(x, str)]
        for key, field in (("category", "categories"), ("technology", "technologies")):
            names = [quote(x) for kk, _, x in v if kk == key]
            if names:
                parts.append(f"{field} = {{ {', '.join(names)} }}")
        return [f"{ind}{recv}:add_tech_bonus {{ {', '.join(parts)} }}"]
    if block and kl == "add_building_construction":
        stats["add_building"] += 1
        extra = [t for t in v if t[0] not in ("type", "level") and not (t[0] == "instant_build" and t[2] == "yes")]
        args = [quote(get(v, "type", "")), lit(get(v, "level", "1"), ctx)] + ([table(extra, ctx)] if extra else [])
        return [f"{ind}{recv}:add_building({', '.join(args)})"]
    if block and kl == "add_popularity":
        stats[kl] += 1
        return [f"{ind}{recv}:add_popularity({quote(get(v, 'ideology', ''))}, {lit(get(v, 'popularity', '0'), ctx)})"]
    if block and kl == "set_rule":
        stats[kl] += 1
        return [f"{ind}{recv}:set_rule({quote(key)}, {lit(x, ctx)})" for key, _, x in v if key and isinstance(x, str)]
    if block and kl == "country_event":
        stats["fire_event"] += 1
        rest = [t for t in v if t[0] != "id"]
        return [f"{ind}{recv}:fire_event({quote(get(v, 'id', ''))}" + (f", {table(rest, ctx)})" if rest else ")")]

    # ---- everything else, by shape
    name = RENAME_EFFECT.get(kl, kl)
    head = method(None if kl in GLOBAL_EFFECTS else recv, name)
    if head is None:
        stats["set_variable"] += 1
        return [f"{ind}{recv}:set_variable({quote(k)}, {table(v, ctx) if block else lit(v, ctx)})"]
    stats[name] += 1
    if block:
        return [f"{ind}{head} {table(v, ctx)}"]
    return [f"{ind}{head}({'' if v == 'yes' else lit(v, ctx)})"]


# ---- focuses ----------------------------------------------------------------------------------------------------------

def function_field(name, body_lines, ind):
    return [f"{ind}{name} = function(c)"] + body_lines + [f"{ind}end,"]


def trigger_field(name, items, stats, ind):
    ctx = Ctx(stats)
    node = combine("all", trig_list(items, ctx, True), True)
    line = f"{ind}{name} = function(c) return {one_line(node)} end,"
    if len(line) <= WIDTH:
        return [line]
    return function_field(name, [f"{ind}  return {fmt(node, ind + '  ', 7)}"], ind)


def weight_field(name, items, stats, ind):
    """ai_will_do and a tree's country block: base, factor or add first, then each modifier whose trigger holds."""
    base = get(items, "base", get(items, "factor", "1"))
    mods = [m for k, _, m in items if k == "modifier" and isinstance(m, list)]
    adds = [x for k, _, x in items if k == "add" and isinstance(x, str)]
    if not mods and not adds:
        return [f"{ind}{name} = {lit(base, Ctx(stats))},"]
    ctx = Ctx(stats)
    lines = [f"{ind}  local w = {lit(base, ctx)}"] + [f"{ind}  w = w + {lit(a, ctx)}" for a in adds]
    for m in mods:
        change = " ".join(f"w = w {'*' if k == 'factor' else '+'} {lit(x, ctx)}" for k, _, x in m
                          if k in ("factor", "add") and isinstance(x, str))
        rest = [t for t in m if t[0] not in ("factor", "add")]
        if not change:
            continue
        cond = condition(rest, ctx, ind + "  ", 8 + len(change) + 4)
        if "\n" in cond or len(ind) + len(cond) + len(change) + 14 > WIDTH:
            lines += [f"{ind}  if {condition(rest, ctx, ind + '  ', 8)} then", f"{ind}    {change}", f"{ind}  end"]
        else:
            lines.append(f"{ind}  if {cond} then {change} end")
    return function_field(name, lines + [f"{ind}  return w"], ind)


def focus_lines(block, stats, ind="  "):
    ctx = Ctx(stats)
    out = [f"{ind}id = {quote(get(block, 'id', ''))},"]
    for k, _, v in block:
        if k in FOCUS_VALUES and isinstance(v, str) and k != "id":
            out.append(f"{ind}{FOCUS_VALUES[k]} = {lit(v, ctx)},")
    # icons chosen by a trigger, offsets: lists of { ..., when = function }
    for key, field, value in (("icon", "icons", "value"), ("offset", "offsets", None)):
        blocks = [v for k, _, v in block if k == key and isinstance(v, list)]
        if not blocks:
            continue
        out.append(f"{ind}{field} = {{")
        for b in blocks:
            parts = [f"{'icon' if kk == value else kk} = {lit(x, ctx)}" for kk, _, x in b if isinstance(x, str)]
            when = get(b, "trigger")
            node = combine("all", trig_list(when, Ctx(stats), False), False) if isinstance(when, list) else None
            if node:
                parts.append(f"when = function(c) return {one_line(node)} end")
            out.append(f"{ind}  {{ {', '.join(parts)} }},")
        out.append(f"{ind}}},")
    # prerequisite blocks: one of each block's focuses is needed, and every block
    groups = [strings_of(v, "focus") for k, _, v in block if k == "prerequisite" and isinstance(v, list)]
    groups = [g for g in groups if g]
    if groups:
        out.append(f"{ind}prerequisites = {{ " + ", ".join("{ " + ", ".join(quote(f) for f in g) + " }" for g in groups) + " },")
    for key, field, inner in (("mutually_exclusive", "mutually_exclusive", "focus"), ("search_filters", "filters", None),
                              ("will_lead_to_war_with", "will_lead_to_war_with", None)):
        names = []
        for k, _, v in block:
            if k == key:
                names += (strings_of(v, inner) if inner else strings(v)) if isinstance(v, list) else [v]
        if names:
            out.append(f"{ind}{field} = {{ {', '.join(quote(n) for n in names)} }},")
    for k, _, v in block:
        if not isinstance(v, list):
            continue
        if k in FOCUS_TRIGGERS:
            # an empty block is no trigger at all: `bypass = { }` never bypasses (it is not "always true"), and an
            # empty `available` holds like a missing one
            if not v:
                continue
            out += trigger_field(FOCUS_TRIGGERS[k], v, stats, ind)
        elif k in FOCUS_EFFECTS:
            out += function_field(FOCUS_EFFECTS[k], effects(v, Ctx(stats), ind + "  "), ind)
        elif k == "ai_will_do":
            out += weight_field("ai", v, stats, ind)
    return out


def strings_of(items, key):
    return [v for k, _, v in items if k == key and isinstance(v, str)]


def translate(text, source="", stats=None):
    """A national_focus file as Lua source."""
    stats = stats if stats is not None else new_stats()
    out = [f"-- Generated by Tools/hoi4_convert.py from {source}: rerun the converter to change it.", ""]

    def focus(kind, block):
        stats["focuses"] += 1
        out.extend([f"{kind} {{"] + focus_lines(block, stats) + ["}", ""])

    for k, _, v in parse_pdx(text):
        if not isinstance(v, list):
            continue
        if k == "focus_tree":
            ctx = Ctx(stats)
            stats["trees"] += 1
            out.append("focus_tree {")
            out.append(f"  id = {quote(get(v, 'id', ''))},")
            for key in ("default", "reset_on_civilwar"):
                if isinstance(get(v, key), str):
                    out.append(f"  {key} = {lit(get(v, key), ctx)},")
            first = get(get(v, "initial_show_position", []), "focus")
            if first:
                out.append(f"  initial_focus = {quote(first)},")
            shared = strings_of(v, "shared_focus")
            if shared:
                out.append(f"  shared = {{ {', '.join(quote(s) for s in shared)} }},")
            if isinstance(get(v, "country"), list):
                out += weight_field("weight", get(v, "country"), stats, "  ")   # the tree with the highest weight is the country's
            out += ["}", ""]
            for kk, _, b in v:
                if kk == "focus" and isinstance(b, list):
                    focus("focus", b)
        elif k in ("shared_focus", "joint_focus"):
            focus(k, v)
    return "\n".join(out)


def new_stats():
    return {"trigger": Counter(), "effect": Counter(), "focuses": 0, "trees": 0}


def write_all(hoi4, out_dir, names=(), stats=None):
    """Translate common/national_focus into out_dir (one .lua per file; files left from an earlier run that no
    longer have a source are removed when every file is translated). Returns {file stem: Lua source}."""
    src = Path(hoi4) / "common" / "national_focus"
    files = [src / f"{n}.txt" for n in names] or sorted(src.glob("*.txt"))
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    stats = stats if stats is not None else new_stats()
    written = {}
    for f in files:
        lua = translate(read_text(f), f"common/national_focus/{f.name}", stats)
        if "focus {" not in lua and "focus_tree {" not in lua:      # a file of other things (title bar styles)
            continue
        written[f.stem] = lua
        target = out_dir / f"{f.stem}.lua"
        if not target.exists() or target.read_text(encoding="utf-8") != lua:
            target.write_text(lua, encoding="utf-8", newline="\n")
    if not names:
        for old in out_dir.glob("*.lua"):
            if old.stem not in written:
                old.unlink()
    return written


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("names", nargs="*", help="files of common/national_focus without .txt (default: all)")
    ap.add_argument("--hoi4", type=Path, default=DEFAULT_HOI4)
    ap.add_argument("--out", type=Path, default=Path(__file__).resolve().parent.parent / "Hoi4Data" / "common" / "focus")
    ap.add_argument("--stats", action="store_true", help="print the triggers and effects the scripts call")
    args = ap.parse_args()
    stats = new_stats()
    written = write_all(args.hoi4, args.out, args.names, stats)
    print(f"{len(written)} file(s), {stats['trees']} trees, {stats['focuses']} focuses -> {args.out}")
    if args.stats:
        for kind in ("trigger", "effect"):
            print(f"{kind}s: {len(stats[kind])} distinct")
            print("  " + ", ".join(f"{n} {c}" for n, c in stats[kind].most_common(60)))


if __name__ == "__main__":
    sys.exit(main())
