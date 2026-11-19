#!/usr/bin/env python3
"""校验与刷新 packages/<包>/manifest.json 的文件摘要，并确定性导出 .mkfx 归档。

只用 Python 标准库。归档布局与 MakoKaraGenerator 后端 EffectPackageTransfer.WriteArchiveAsync 一致：
先写 manifest.json，再按 ordinal 路径顺序写声明文件；固定 2000-01-01 00:00:00 时间戳、Deflate、
外部属性为 0，不写目录条目。导出归档默认写入 dist/ 目录。
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PACKAGES_ROOT = ROOT / "packages"
DEFAULT_DIST = ROOT / "dist"
FIXED_TIME = (2000, 1, 1, 0, 0, 0)
EXPECTED_SHADER_PROFILE = "skia-ganesh-3.119.4-v2"


def reject_duplicates(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"JSON 重复键：{key}")
        result[key] = value
    return result


def load_manifest(directory: Path) -> dict:
    text = (directory / "manifest.json").read_text(encoding="utf-8")
    return json.loads(text, object_pairs_hook=reject_duplicates)


def disk_files(directory: Path) -> set[str]:
    found: set[str] = set()
    for path in directory.rglob("*"):
        if path.is_symlink():
            raise ValueError(f"包内不能有符号链接：{path}")
        relative = path.relative_to(directory).as_posix()
        if path.is_file() and relative != "manifest.json":
            found.add(relative)
    return found


def check_contract(manifest: dict) -> list[str]:
    problems: list[str] = []
    if manifest.get("packageVersion") != 3:
        problems.append(f"packageVersion 必须为 3，当前为 {manifest.get('packageVersion')!r}")
    sdk = str(manifest.get("sdkVersion", ""))
    parts = sdk.split(".")
    if len(parts) != 3 or parts[0] != "2" or parts[1] != "0" or not parts[2].isdigit():
        problems.append(f"sdkVersion 必须为 2.0.x，当前为 {sdk!r}")
    if manifest.get("shaderProfile") != EXPECTED_SHADER_PROFILE:
        problems.append(
            f"shaderProfile 必须为 {EXPECTED_SHADER_PROFILE}，当前为 {manifest.get('shaderProfile')!r}"
        )
    return problems


def build_archive(directory: Path, manifest_bytes: bytes, paths: list[str]) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        items = [("manifest.json", manifest_bytes)] + [
            (p, (directory / p).read_bytes()) for p in paths
        ]
        for name, content in items:
            info = zipfile.ZipInfo(name, FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 0
            info.external_attr = 0
            archive.writestr(info, content, compresslevel=9)
    return buffer.getvalue()


def resolve_package_dirs(names: list[str]) -> list[Path]:
    if not names:
        return sorted(
            (p for p in PACKAGES_ROOT.iterdir() if p.is_dir() and (p / "manifest.json").is_file()),
            key=lambda p: p.name,
        )
    dirs: list[Path] = []
    for raw in names:
        cleaned = raw.strip().rstrip("/")
        if cleaned.startswith("packages/"):
            cleaned = cleaned[len("packages/") :]
        candidate = PACKAGES_ROOT / cleaned
        if not (candidate / "manifest.json").is_file():
            raise FileNotFoundError(f"找不到包目录或 manifest.json：{candidate}")
        dirs.append(candidate)
    return dirs


def process(directory: Path, check: bool, refresh_only: bool, output_dir: Path) -> list[str]:
    problems: list[str] = []
    manifest = load_manifest(directory)
    problems.extend(check_contract(manifest))

    entries = manifest["files"]
    declared = [entry["path"] for entry in entries]
    if len(set(declared)) != len(declared):
        problems.append("files 中存在重复路径")

    actual = disk_files(directory)
    if missing := sorted(set(declared) - actual):
        problems.append("清单已声明但磁盘缺失：" + ", ".join(missing))
    if extra := sorted(actual - set(declared)):
        problems.append("磁盘存在但清单未声明：" + ", ".join(extra))
    if problems:
        return problems

    for entry in entries:
        digest = hashlib.sha256((directory / entry["path"]).read_bytes()).hexdigest()
        if entry["sha256"] != digest:
            if check:
                problems.append(f"SHA-256 过期：{entry['path']}")
            entry["sha256"] = digest

    manifest_bytes = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    manifest_path = directory / "manifest.json"
    if manifest_path.read_bytes() != manifest_bytes:
        if check:
            problems.append("manifest.json 与规范格式化或最新摘要不一致")
        else:
            manifest_path.write_bytes(manifest_bytes)

    if check or refresh_only:
        return problems

    output_dir.mkdir(parents=True, exist_ok=True)
    archive = build_archive(directory, manifest_bytes, sorted(declared))
    target = output_dir / f"{directory.name}.mkfx"
    if not target.exists() or target.read_bytes() != archive:
        target.write_bytes(archive)
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="只校验目录完整性、合同字段与 SHA-256，不写任何文件")
    mode.add_argument("--refresh", action="store_true", help="只刷新 manifest.json 中的 SHA-256，不生成 .mkfx")
    parser.add_argument(
        "-o", "--output", type=Path, default=DEFAULT_DIST,
        help=f".mkfx 输出目录（默认：{DEFAULT_DIST.relative_to(ROOT)}）",
    )
    parser.add_argument("packages", nargs="*", help="packages 下的包目录名；省略时处理全部")
    args = parser.parse_args()

    try:
        dirs = resolve_package_dirs(args.packages)
    except Exception as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 2

    if not dirs:
        print("packages/ 下未找到任何效果包目录。", file=sys.stderr)
        return 1

    failed = False
    for directory in dirs:
        try:
            problems = process(directory, args.check, args.refresh, args.output)
        except Exception as exc:
            problems = [f"{type(exc).__name__}: {exc}"]
        if problems:
            status = "失败"
        elif args.check:
            status = "通过"
        elif args.refresh:
            status = "已刷新摘要"
        else:
            status = f"已打包 -> {args.output / (directory.name + '.mkfx')}"
        print(f"{directory.name}: {status}")
        for problem in problems:
            print("  - " + problem)
        failed |= bool(problems)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
