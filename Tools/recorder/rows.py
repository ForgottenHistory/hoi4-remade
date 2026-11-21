"""The engine's rows.py on this project's recordings: python rows.py [REC] TABLE [filters] (see its --help)."""
import os
import runpy

import _engine

runpy.run_path(os.path.join(_engine.ENGINE_TOOLS, "rows.py"), run_name="__main__")
