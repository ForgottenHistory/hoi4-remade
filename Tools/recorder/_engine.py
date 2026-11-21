"""Points the engine's recording tools (Assets/Archon-Engine/Tools~/recording) at this project's Recordings/."""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
ENGINE_TOOLS = os.path.join(PROJECT, "Assets", "Archon-Engine", "Tools~", "recording")

os.environ.setdefault("ARCHON_RECORDINGS", os.path.join(PROJECT, "Recordings"))
if ENGINE_TOOLS not in sys.path:
    sys.path.insert(0, ENGINE_TOOLS)
