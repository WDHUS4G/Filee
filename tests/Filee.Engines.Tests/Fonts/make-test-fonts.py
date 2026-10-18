# Rebuilds the small test fonts in this folder from the Noto fonts that build/fetch-fonts.ps1 downloads (SIL OFL 1.1).
# Only needed when the test fonts change: the tests use the committed files and never run this script.
#
#   pip install fonttools==4.66.1
#   npm install wawoff2@2.0.1        (optional)
#   python make-test-fonts.py <folder with NotoSans-Regular.ttf and NotoSansKR-Regular.otf> [woff2_compress.js]
#
# The optional second argument is node_modules/wawoff2/bin/woff2_compress.js (Google's reference WOFF2 encoder,
# github.com/google/woff2, compiled to WebAssembly); with it the script also writes FileeTest-TrueType.google.woff2.

import os
import subprocess
import sys

from fontTools import subset
from fontTools.cffLib import SubrsIndex
from fontTools.fontBuilder import FontBuilder
from fontTools.misc.psCharStrings import T2CharString
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.t2CharStringPen import T2CharStringPen
from fontTools.ttLib import TTFont

HERE = os.path.dirname(os.path.abspath(__file__))


def rename(font, family, ps_name):
    """Gives the subset its own name, so a modified font doesn't pass for the original (see OFL.txt)."""
    name = font["name"]
    name.names = [n for n in name.names if n.nameID in (0, 13, 14)]
    for name_id, value in {1: family, 2: "Regular", 3: ps_name + ";Filee test font", 4: family + " Regular",
                           5: "Version 1.000", 6: ps_name}.items():
        name.setName(value, name_id, 3, 1, 0x409)
        name.setName(value, name_id, 1, 0, 0)
    if "CFF " in font:
        cff = font["CFF "].cff
        old = cff.fontNames[0]
        cff.fontNames = [ps_name]
        top = cff.topDictIndex[0]
        top.FullName = family + " Regular"
        top.FamilyName = family
        for fd in getattr(top, "FDArray", []):
            fd.FontName = fd.FontName.replace(old, ps_name)


def subset_font(source, text, **options):
    opts = subset.Options()  # default layout features: kern, liga, mark, vert, ...
    opts.name_IDs = ["*"]
    opts.name_languages = ["*"]
    opts.notdef_outline = True
    opts.glyph_names = True
    opts.drop_tables += ["DSIG"]
    for key, value in options.items():
        setattr(opts, key, value)
    font = TTFont(source)
    subsetter = subset.Subsetter(opts)
    subsetter.populate(text=text)
    subsetter.subset(font)
    return font


def truetype(fonts):
    """Hinted TrueType glyphs with composites (accented letters), kerning, and overlap flags set on purpose."""
    font = subset_font(os.path.join(fonts, "NotoSans-Regular.ttf"),
                       "Filee Hamburgefonstiv ÁáéÅçÑü 0123 .,!-")
    glyf = font["glyf"]
    glyf["A"].flags[0] |= 0x40  # OVERLAP_SIMPLE
    glyf["Aacute"].components[0].flags |= 0x400  # OVERLAP_COMPOUND
    rename(font, "Filee Test TrueType", "FileeTest-TrueType")
    path = os.path.join(HERE, "FileeTest-TrueType.ttf")
    font.save(path)
    return path


def cid_keyed(fonts):
    """CID-keyed CFF with local and global subroutines, hints, hint masks, VORG and vertical metrics."""
    font = subset_font(os.path.join(fonts, "NotoSansKR-Regular.otf"), "Filee 한글 변환 Hamburg 0123 ,.")
    rename(font, "Filee Test CID", "FileeTest-CID")
    path = os.path.join(HERE, "FileeTest-CID.otf")
    font.save(path)


def name_keyed(fonts):
    """Name-keyed CFF: glyph names, an `endchar` accent (seac), flex, and local and global subroutines."""
    source = subset_font(os.path.join(fonts, "NotoSansKR-Regular.otf"), " Aae´", desubroutinize=True)
    cmap = source.getBestCmap()
    glyph_set = source.getGlyphSet()
    names = {"space": 0x20, "A": ord("A"), "a": ord("a"), "e": ord("e"), "acute": 0xB4}

    order = [".notdef", "space", "A", "a", "e", "acute", "Aacute", "flexbar", "box"]
    char_strings, metrics = {}, {}

    def add(name, source_name):
        width = source["hmtx"][source_name][0]
        pen = T2CharStringPen(width, glyph_set)
        glyph_set[source_name].draw(pen)
        char_strings[name] = pen.getCharString()
        bounds = BoundsPen(glyph_set)
        glyph_set[source_name].draw(bounds)
        metrics[name] = (width, bounds.bounds[0] if bounds.bounds else 0)

    add(".notdef", ".notdef")
    for name, code in names.items():
        add(name, cmap[code])

    # Aacute = A + acute moved up: StandardEncoding codes 65 (A) and 194 (acute).
    a_width = metrics["A"][0]
    char_strings["Aacute"] = T2CharString(program=[a_width, 170, 190, 65, 194, "endchar"])
    metrics["Aacute"] = (a_width, metrics["A"][1])
    # A bar whose top edge is a flex (12 35), drawn counter-clockwise like every CFF outer contour.
    char_strings["flexbar"] = T2CharString(program=[
        700, 100, 0, "rmoveto", 500, 0, "rlineto", 0, 500, "rlineto",
        -100, 20, -50, 10, -100, 0, -100, 0, -50, -10, -100, -20, 50, "flex", "endchar"])
    metrics["flexbar"] = (700, 100)
    # A square with a hole: the outer contour comes from a local subroutine, the inner one from a global one.
    char_strings["box"] = T2CharString(program=[600, -107, "callsubr", -107, "callgsubr", "endchar"])
    metrics["box"] = (600, 50)

    builder = FontBuilder(1000, isTTF=False)
    builder.setupGlyphOrder(order)
    builder.setupCharacterMap({**{code: name for name, code in names.items()}, 0xC1: "Aacute",
                               0xE000: "flexbar", 0xE001: "box"})
    builder.setupCFF("FileeTest-NameKeyed", {"FullName": "Filee Test NameKeyed Regular"}, char_strings, {})
    cff = builder.font["CFF "].cff
    top = cff.topDictIndex[0]
    subrs = SubrsIndex()
    subrs.append(T2CharString(program=[50, 0, "rmoveto", 500, "hlineto", 500, "vlineto", -500, "hlineto", "return"]))
    top.Private.Subrs = subrs
    cff.GlobalSubrs.append(T2CharString(program=[100, -400, "rmoveto", 0, 300, "rlineto", 300, 0, "rlineto",
                                                 0, -300, "rlineto", "return"]))
    for cs in char_strings.values():
        cs.private = top.Private
        cs.globalSubrs = cff.GlobalSubrs
    builder.setupHorizontalMetrics(metrics)
    builder.setupHorizontalHeader(ascent=880, descent=-120)
    builder.setupNameTable({"familyName": "Filee Test NameKeyed", "styleName": "Regular",
                            "copyright": source["name"].getDebugName(0),
                            "licenseDescription": "This Font Software is licensed under the SIL Open Font License, Version 1.1.",
                            "licenseInfoURL": "https://openfontlicense.org"})
    builder.setupOS2(sTypoAscender=880, sTypoDescender=-120, usWinAscent=1160, usWinDescent=288,
                     achVendID="FILE", usWeightClass=400)
    builder.setupPost(keepGlyphNames=False)
    builder.save(os.path.join(HERE, "FileeTest-NameKeyed.otf"))


def main():
    fonts = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "..", "..", "src", "Filee.App", "Assets", "Fonts")
    ttf = truetype(fonts)
    cid_keyed(fonts)
    name_keyed(fonts)
    if len(sys.argv) > 2:
        subprocess.run(["node", sys.argv[2], ttf, os.path.join(HERE, "FileeTest-TrueType.google.woff2")], check=True)


if __name__ == "__main__":
    main()
