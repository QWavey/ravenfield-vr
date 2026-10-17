"""Builds the sandbox into a Windows program: tools/sandbox/bin/RavenfieldVR-Sandbox/RavenfieldVR-Sandbox.exe
(and a shortcut-friendly copy of the folder's exe path printed at the end).

    python tools/sandbox/build_exe.py

The program finds the mod's folder by looking upward from where it is (keep it under tools/sandbox/bin, or it asks once).
"""
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.join(HERE, 'app')
BIN = os.path.join(HERE, 'bin')
WORK = os.path.join(HERE, 'build')
ICON = os.path.join(APP, 'icon.ico')


def make_icon():
    """A small icon: the glove's colour round a crosshair, made once."""
    if os.path.exists(ICON):
        return
    from PIL import Image, ImageDraw
    size = 256
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((8, 8, size - 8, size - 8), radius=56, fill=(38, 41, 47, 255))
    d.ellipse((52, 52, size - 52, size - 52), outline=(215, 155, 69, 255), width=18)
    c = size // 2
    for a, b in (((c, 30), (c, 96)), ((c, size - 96), (c, size - 30)), ((30, c), (96, c)), ((size - 96, c), (size - 30, c))):
        d.line((a, b), fill=(215, 155, 69, 255), width=16)
    d.ellipse((c - 14, c - 14, c + 14, c + 14), fill=(230, 230, 230, 255))
    img.save(ICON, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


def main():
    make_icon()
    args = [sys.executable, '-m', 'PyInstaller', '--noconfirm', '--clean', '--windowed', '--name', 'RavenfieldVR-Sandbox',
            '--icon', ICON, '--distpath', BIN, '--workpath', WORK, '--specpath', WORK, '--paths', APP,
            '--collect-data', 'qfluentwidgets', '--hidden-import', 'OpenGL.platform.win32',
            '--add-data', f'{ICON}{os.pathsep}.']
    # Big packages this Python has that the app never uses (PyInstaller followed their optional imports: 1.7 GB).
    for m in ('tensorflow', 'torch', 'torchvision', 'pandas', 'grpc', 'h5py', 'onnx', 'onnxruntime', 'lxml', 'matplotlib', 'IPython',
              'jupyter', 'notebook', 'sklearn', 'cv2', 'jax', 'numba', 'sympy', 'tornado', 'zmq', 'tensorboard', 'keras', 'transformers',
              'cryptography', 'pytest', 'setuptools', 'PyQt5', 'PySide6', 'PySide2', 'tkinter'):
        args += ['--exclude-module', m]
    args.append(os.path.join(APP, 'main.py'))
    print(' '.join(args))
    subprocess.check_call(args, cwd=HERE)
    exe = os.path.join(BIN, 'RavenfieldVR-Sandbox', 'RavenfieldVR-Sandbox.exe')
    print('built', exe)


if __name__ == '__main__':
    main()
