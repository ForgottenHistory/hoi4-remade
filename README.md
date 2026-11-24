# hoi4-remade

An experimental remake of Hearts of Iron IV in Unity

Made from [Archon Engine](https://github.com/ForgottenHistory/Archon-Engine), a grand strategy engine.

![The world on 1 January 1936](Images/world.png)

## What works so far

Most features are developed from testing HOI4, reading real HOI4 data. Treat most of these as first versions

- **Map:** HOI4 terrain and water, smooth borders, rivers, country labels, haze and clouds, day/night with city lights, political, terrain and diplomatic map modes
- **Divisions:** the 1936 armies, on-map counters, movement by HOI4's rules, province-to-province combat with terrain, rivers and entrenchment, retreat, occupation, manpower and equipment with reinforcement
- **Armies and battle plans:** army cards, front lines that divisions fill and hold, offensives with the planning bonus
- **War and peace:** war participation, surrender progress, capitulation and a basic peace conference
- **Production:** military factories, production lines with efficiency, the production screen
- **Construction:** civilian factories and consumer goods, the construction queue, placing buildings on the map
- **Recruit and deploy:** training lines that use manpower, equipment and training time, deployment orders
- **Division designer:** battalions, support and regimental support companies, live stats, saving and duplicating templates
- **AI:** a layered war AI (theatres, armies holding front-line sections, attacks by odds), army plans with role templates, and production, construction and recruitment AI
- **Simulation:** deterministic and command-based (fixed-point maths, one hourly tick), with session recordings for debugging

Not there yet: research, politics, trade, supply, generals, air and naval, factions, saving HOI4 state, multiplayer, etc.

The full list is in [`CURRENT_FEATURES.md`](CURRENT_FEATURES.md).

## Requirements

- Unity 6000.3 (URP)
- An installed copy of Hearts of Iron IV
- Python 3 with `numpy`, `scipy` and `Pillow`

## Setup

```
git clone --recursive https://github.com/ForgottenHistory/hoi4-remade.git
python Tools/hoi4_convert.py --hoi4 "<path to your HOI4 install>" --out Hoi4Data
```

The converter reads the map, history, localisation and graphics from your own HOI4 install and writes them to `Hoi4Data/`. Without `--hoi4` it looks for the Steam install under `C:\Program Files (x86)`. Then open `Assets/Scenes/Hoi4Test.unity` in Unity and press Play.

## Images

![Armies and battle plans](Images/armies.png)

![Construction](Images/construction.png)

![Division designer](Images/division_designer.png)

## Disclaimer

This is an unofficial, non-commercial fan project made for learning and for testing a game engine. It is not affiliated with, endorsed by or sponsored by Paradox Interactive. Hearts of Iron, Paradox Interactive and their logos are trademarks of Paradox Interactive AB, and Hearts of Iron IV and all of its content (art, maps, text, data and code) are the property of Paradox Interactive.

- **No game files.** This repository contains no Paradox assets, game data or game code. The game mechanics are reimplemented from scratch. To run it you need your own legally obtained copy of Hearts of Iron IV, which the converter reads on your machine.
- **Don't share the converted data.** `Hoi4Data/` is made from your copy of the game. It is gitignored and must never be uploaded or redistributed.
- **Screenshots.** The images in this README show HOI4's art and interface, loaded at runtime from a local copy of the game. They are here only to show the project.
- **Not for sale.** Nothing here may be sold, and no build of it that includes Paradox content may be distributed.

If you represent Paradox Interactive and want anything changed or removed, open an issue and it will be dealt with promptly.

## License

The code in this repository is MIT licensed (see `LICENSE`). The engine has its own license, in `Assets/Archon-Engine`.
