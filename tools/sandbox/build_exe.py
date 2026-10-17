"""Builds the sandbox into a Windows program: tools/sandbox/bin/RavenfieldVR-Sandbox/RavenfieldVR-Sandbox.exe
(and a shortcut-friendly copy of the folder's exe path printed at the end).

    python tools/sandbox/build_exe.py

The program finds the mod's folder by looking upward from where it is (keep it under tools/sandbox/bin, or it asks once).
"""
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.join(HERE, 'app')
BIN = os.path.join(HERE, 'bin')
WORK = os.path.join(HERE, 'build')
ICON = os.path.join(APP, 'icon.ico')
VERSION = '1.1.0.0'  # the exe's file version (Properties > Details)

# Qt modules the sandbox never imports. The sandbox and qfluentwidgets use QtCore, QtGui, QtWidgets, QtOpenGL, QtOpenGLWidgets,
# QtSvg and QtXml; everything here is a module PyInstaller would otherwise follow into (PDF, web engine, QML, 3D, media...).
UNUSED_QT = ('QtPdf', 'QtPdfWidgets', 'QtWebEngineCore', 'QtWebEngineWidgets', 'QtWebEngineQuick', 'QtQml', 'QtQuick',
             'QtQuickWidgets', 'QtQuick3D', 'Qt3DCore', 'Qt3DRender', 'Qt3DInput', 'Qt3DLogic', 'Qt3DAnimation', 'Qt3DExtras',
             'QtMultimedia', 'QtMultimediaWidgets', 'QtBluetooth', 'QtNfc', 'QtPositioning', 'QtSensors', 'QtSerialPort',
             'QtWebSockets', 'QtDesigner')

# The exe's version resource (Properties > Details, Task Manager). PyInstaller reads this file as Python source.
VERSION_INFO = """VSVersionInfo(
  ffi=FixedFileInfo(
    filevers=({numbers}),
    prodvers=({numbers}),
    mask=0x3F,
    flags=0x0,
    OS=0x40004,
    fileType=0x1,
    subtype=0x0,
    date=(0, 0)
  ),
  kids=[
    StringFileInfo([
      StringTable('040904B0', [
        StringStruct('CompanyName', 'QWavey'),
        StringStruct('FileDescription', 'Ravenfield VR Sandbox'),
        StringStruct('FileVersion', '{version}'),
        StringStruct('InternalName', 'RavenfieldVR-Sandbox'),
        StringStruct('OriginalFilename', 'RavenfieldVR-Sandbox.exe'),
        StringStruct('ProductName', 'Ravenfield VR Sandbox'),
        StringStruct('ProductVersion', '{version}')])
    ]),
    VarFileInfo([VarStruct('Translation', [1033, 1200])])
  ]
)
"""


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


def write_version_file():
    """The version resource, written to a temporary file: PyInstaller's --clean empties its build folder before it reads it."""
    path = os.path.join(tempfile.gettempdir(), 'RavenfieldVR-Sandbox-version.txt')
    with open(path, 'w', encoding='utf-8') as f:
        f.write(VERSION_INFO.format(numbers=', '.join(VERSION.split('.')), version=VERSION))
    return path


def unused_paths(internal):
    """What PyInstaller still copies although the sandbox never uses it: the Qt translations (no translator is ever installed),
    Qt's PDF image plug-in (it links the PDF DLL), the DLLs of the Qt modules above, and PyOpenGL's GLUT and GLE DLLs (only GLUT
    loads them, and the sandbox does not use GLUT)."""
    qt = os.path.join(internal, 'PyQt6', 'Qt6')
    paths = [os.path.join(qt, 'translations'), os.path.join(qt, 'plugins', 'imageformats', 'qpdf.dll'),
             os.path.join(internal, 'OpenGL', 'DLLS')]
    for m in UNUSED_QT:
        paths.append(os.path.join(internal, 'PyQt6', m + '.pyd'))
        paths.append(os.path.join(qt, 'bin', 'Qt6' + m[2:] + '.dll'))  # (Qt3DCore is Qt63DCore.dll)
    return paths


def prune(internal):
    """Deletes what unused_paths lists. A path that is not there is skipped (PyInstaller's layout can change between versions),
    and a file that cannot be removed (in use) is reported and left: the program runs without it."""
    for p in unused_paths(internal):
        try:
            if os.path.isdir(p):
                shutil.rmtree(p)
            elif os.path.exists(p):
                os.remove(p)
            else:
                continue
            print('removed', p)
        except OSError as e:
            print(f'could not remove {p}: {e}')


def main():
    make_icon()
    version = write_version_file()
    args = [sys.executable, '-m', 'PyInstaller', '--noconfirm', '--clean', '--windowed', '--name', 'RavenfieldVR-Sandbox',
            '--icon', ICON, '--version-file', version, '--distpath', BIN, '--workpath', WORK, '--specpath', WORK, '--paths', APP,
            '--collect-data', 'qfluentwidgets', '--hidden-import', 'OpenGL.platform.win32',
            '--add-data', f'{ICON}{os.pathsep}.']
    # Big packages this Python has that the app never uses (PyInstaller followed their optional imports: 1.7 GB).
    for m in ('tensorflow', 'torch', 'torchvision', 'pandas', 'grpc', 'h5py', 'onnx', 'onnxruntime', 'lxml', 'matplotlib', 'IPython',
              'jupyter', 'notebook', 'sklearn', 'cv2', 'jax', 'numba', 'sympy', 'tornado', 'zmq', 'tensorboard', 'keras', 'transformers',
              'cryptography', 'pytest', 'setuptools', 'PyQt5', 'PySide6', 'PySide2', 'tkinter'):
        args += ['--exclude-module', m]
    # The Qt parts above (PyQt6's own module names), so their hooks do not copy their files in.
    for m in UNUSED_QT:
        args += ['--exclude-module', 'PyQt6.' + m]
    args.append(os.path.join(APP, 'main.py'))
    print(' '.join(args))
    try:
        subprocess.check_call(args, cwd=HERE)
    finally:
        os.remove(version)
    prune(os.path.join(BIN, 'RavenfieldVR-Sandbox', '_internal'))
    exe = os.path.join(BIN, 'RavenfieldVR-Sandbox', 'RavenfieldVR-Sandbox.exe')
    print('built', exe)


if __name__ == '__main__':
    main()
