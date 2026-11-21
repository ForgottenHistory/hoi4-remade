# hoi4-remade

An experimental remake of Hearts of Iron IV in Unity

Made from [Archon Engine](https://github.com/ForgottenHistory/Archon-Engine), a grand strategy engine

So far it has:

- **Map:** terrain, borders, rivers, labels, atmosphere, day/night
- **Armies:** divisions, armies, battle plans, combat and an enemy AI

The full list is in `CURRENT_FEATURES.md`.

## Requirements

- Unity 6000.3 (URP)
- An installed copy of Hearts of Iron IV
- Python 3 with `numpy`, `scipy` and `Pillow`

## Setup

```
git clone --recursive https://github.com/ForgottenHistory/hoi4-remade.git
python Tools/hoi4_convert.py --hoi4 "<path to your HOI4 install>" --out Hoi4Data
```

The converter reads the map, history, localisation and graphics from your own HOI4 install and writes them to `Hoi4Data/`. Then open `Assets/Scenes/Hoi4Test.unity` in Unity and press Play.

## Disclaimer

This is an unofficial, non-commercial fan project. It is not affiliated with or endorsed by Paradox Interactive. Hearts of Iron IV and its content are the property of Paradox Interactive. This repository contains no Paradox assets or game data: everything is read from your own copy of the game at conversion time, and `Hoi4Data/` must never be redistributed.

## License

The code in this repository is MIT licensed (see `LICENSE`). The engine has its own license, in `Assets/Archon-Engine`.
