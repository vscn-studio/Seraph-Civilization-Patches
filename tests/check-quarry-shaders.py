"""Compile/link quarry shaders on Windows with a hidden native OpenGL context.

Usage: python tests/check-quarry-shaders.py [Vintage Story installation]
Uses the game's include files and graphics defines; requires no Python packages.
"""
import ctypes as c
from ctypes import wintypes as w
from pathlib import Path
import re
import sys


class PixelFormat(c.Structure):
    _fields_ = [("size", w.WORD), ("version", w.WORD), ("flags", w.DWORD),
                ("type", w.BYTE), ("color", w.BYTE), ("red", w.BYTE), ("redshift", w.BYTE),
                ("green", w.BYTE), ("greenshift", w.BYTE), ("blue", w.BYTE), ("blueshift", w.BYTE),
                ("alpha", w.BYTE), ("alphashift", w.BYTE), ("accum", w.BYTE),
                ("accumred", w.BYTE), ("accumgreen", w.BYTE), ("accumblue", w.BYTE), ("accumalpha", w.BYTE),
                ("depth", w.BYTE), ("stencil", w.BYTE), ("aux", w.BYTE), ("layer", w.BYTE),
                ("reserved", w.BYTE), ("layermask", w.DWORD), ("visiblemask", w.DWORD), ("damagemask", w.DWORD)]


def bind(lib, name, result, *args):
    func = getattr(lib, name)
    func.restype, func.argtypes = result, args
    return func


user, gdi, gl = c.WinDLL("user32"), c.WinDLL("gdi32"), c.WinDLL("opengl32")
create_window = bind(user, "CreateWindowExW", w.HWND, w.DWORD, w.LPCWSTR, w.LPCWSTR, w.DWORD,
                     c.c_int, c.c_int, c.c_int, c.c_int, w.HWND, w.HMENU, w.HINSTANCE, c.c_void_p)
window = create_window(0, "STATIC", "Quarry shader checks", 0, 0, 0, 16, 16, None, None, None, None)
assert window, "Cannot create hidden test window"
dc = bind(user, "GetDC", w.HDC, w.HWND)(window)
context = None
make_current = bind(gl, "wglMakeCurrent", w.BOOL, w.HDC, c.c_void_p)
try:
    pixel = PixelFormat(size=c.sizeof(PixelFormat), version=1, flags=0x25, color=32, depth=24)
    fmt = bind(gdi, "ChoosePixelFormat", c.c_int, w.HDC, c.POINTER(PixelFormat))(dc, c.byref(pixel))
    assert fmt and bind(gdi, "SetPixelFormat", w.BOOL, w.HDC, c.c_int, c.POINTER(PixelFormat))(dc, fmt, c.byref(pixel))
    context = bind(gl, "wglCreateContext", c.c_void_p, w.HDC)(dc)
    assert context and make_current(dc, context), "Cannot create OpenGL context"
    print("OpenGL:", bind(gl, "glGetString", c.c_char_p, c.c_uint)(0x1F02).decode())
    get_proc = bind(gl, "wglGetProcAddress", c.c_void_p, c.c_char_p)

    def proc(name, result, *args):
        address = get_proc(name.encode())
        assert address and address not in (1, 2, 3, c.c_void_p(-1).value), name + " unavailable"
        return c.WINFUNCTYPE(result, *args)(address)

    new_shader = proc("glCreateShader", c.c_uint, c.c_uint)
    source = proc("glShaderSource", None, c.c_uint, c.c_int, c.POINTER(c.c_char_p), c.c_void_p)
    compile_shader = proc("glCompileShader", None, c.c_uint)
    shader_status = proc("glGetShaderiv", None, c.c_uint, c.c_uint, c.POINTER(c.c_int))
    shader_log = proc("glGetShaderInfoLog", None, c.c_uint, c.c_int, c.c_void_p, c.c_void_p)
    new_program = proc("glCreateProgram", c.c_uint)
    attach = proc("glAttachShader", None, c.c_uint, c.c_uint)
    link = proc("glLinkProgram", None, c.c_uint)
    program_status = proc("glGetProgramiv", None, c.c_uint, c.c_uint, c.POINTER(c.c_int))
    program_log = proc("glGetProgramInfoLog", None, c.c_uint, c.c_int, c.c_void_p, c.c_void_p)
    delete_shader = proc("glDeleteShader", None, c.c_uint)
    delete_program = proc("glDeleteProgram", None, c.c_uint)
    game = Path(sys.argv[1] if len(sys.argv) > 1 else "E:/vintagestory/Vintagestory")
    assets = Path(__file__).resolve().parents[1] / "assets/vsscpmod/shaders"

    def expand(code, seen):
        def include(match):
            name = match[1].strip().lower()
            if name in seen:
                return ""
            seen.add(name)
            return expand((game / "assets/game/shaderincludes" / name).read_text(encoding="utf-8-sig"), seen)
        return re.sub(r"^#include\s+([^\r\n]+)", include, code, flags=re.M)

    compiled = 0
    for shadow in (0, 1, 2):
        for ssao in (0, 1, 2):
            for lights in (0, 8):
                defines = dict(SHADOWQUALITY=shadow, SSAOLEVEL=ssao, DYNLIGHTS=lights, NORMALVIEW=0,
                               BLOOM=1, GODRAYS=1, FOAMEFFECT=1, SHINYEFFECT=1, WAVINGSTUFF=1,
                               USESSBO=0, USEOIT=0, MINBRIGHT=0, MAXANIMATEDELEMENTS=46, FXAA=0)
                prefix = "\n".join(f"#define {key} {value}" for key, value in defines.items()) + "\n"
                for name in ("quarryload", "quarryloadshadow"):
                    program, shaders = new_program(), []
                    try:
                        for extension, kind in (("vsh", 0x8B31), ("fsh", 0x8B30)):
                            code = expand((assets / (name + "." + extension)).read_text(encoding="utf-8-sig"), set())
                            version, body = code.split("\n", 1)
                            shader = new_shader(kind)
                            shaders.append(shader)
                            source(shader, 1, c.byref(c.c_char_p((version + "\n" + prefix + body).encode())), None)
                            compile_shader(shader)
                            status, log = c.c_int(), c.create_string_buffer(65536)
                            shader_status(shader, 0x8B81, c.byref(status))
                            shader_log(shader, len(log), None, log)
                            assert status.value, f"{name}.{extension} {defines}: {log.value.decode()}"
                            attach(program, shader)
                        link(program)
                        status, log = c.c_int(), c.create_string_buffer(65536)
                        program_status(program, 0x8B82, c.byref(status))
                        program_log(program, len(log), None, log)
                        assert status.value, f"{name} link {defines}: {log.value.decode()}"
                        compiled += 1
                    finally:
                        delete_program(program)
                        for shader in shaders:
                            delete_shader(shader)
    print(f"PASS: {compiled} quarry shader programs compiled and linked across shadow/SSAO/light settings")
finally:
    make_current(None, None)
    if context:
        bind(gl, "wglDeleteContext", w.BOOL, c.c_void_p)(context)
    bind(user, "ReleaseDC", c.c_int, w.HWND, w.HDC)(window, dc)
    bind(user, "DestroyWindow", w.BOOL, w.HWND)(window)
