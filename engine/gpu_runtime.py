"""Register NVIDIA's environment-local Windows runtime DLLs before importing CT2."""
import os
import sys
import ctypes
from pathlib import Path

_handles = []


def configure():
    # Anaconda 3.12 ships MSVCP 14.29, which crashes recent MSVC mutex code.
    # Prefer the already installed system C++ runtime before importing CT2/numpy.
    cpp_runtime = Path(sys.prefix) / 'msvcp140.dll'
    if not cpp_runtime.is_file():
        cpp_runtime = Path(os.environ['SystemRoot']) / 'System32' / 'msvcp140.dll'
    _handles.append(ctypes.WinDLL(str(cpp_runtime)))
    root = Path(sys.prefix) / 'Lib' / 'site-packages' / 'nvidia'
    directories = sorted({p.parent for p in root.rglob('*.dll')}) if root.exists() else []
    for directory in directories:
        _handles.append(os.add_dll_directory(str(directory)))
    os.environ['PATH'] = os.pathsep.join(map(str, directories)) + os.pathsep + os.environ.get('PATH', '')
    os.environ.setdefault('HF_HOME', str(Path(__file__).parent / 'models' / 'huggingface'))
    os.environ.setdefault('HF_HUB_DISABLE_SYMLINKS_WARNING', '1')
    os.environ.setdefault('HF_HUB_DISABLE_XET', '1')
    return directories
