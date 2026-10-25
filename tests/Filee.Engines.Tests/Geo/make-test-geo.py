"""Writes the Shapefile test fixtures in this folder with GDAL (through Fiona).

Why they exist: GeoShapefileTests and GeoFormatTests read Shapefiles that an independent implementation wrote, so a
mistake in Filee's own reader can't cancel out against the same mistake in Filee's own writer. The data is synthetic
(a few points, polygons and lines made up for the tests): no third-party data, nothing to license. GDAL itself is not
shipped. Everything else the tests need (GeoJSON, KML, GPX, CSV and the damaged or unusual Shapefiles) is written in
the tests, see GeoBuilders.cs.

    pip install fiona pyproj
    python make-test-geo.py

The files are rewritten next to this script. The .dbf files hold the date they were written on, so a regenerated set
differs from the committed one in those bytes only; the tests don't depend on them.

Sets: gdal_points (every attribute type, Korean / accented / CJK text), gdal_polygons (a hole; a multi-part polygon),
gdal_linesz (3D lines), gdal_utm33 (points in UTM zone 33N with an ESRI .prj, positions from PROJ).
"""
import os
import warnings

import fiona
from fiona.crs import CRS
from pyproj import Transformer

warnings.filterwarnings("ignore")
OUT = os.path.dirname(os.path.abspath(__file__))
wgs = CRS.from_epsg(4326)

# Points with every attribute type; Korean, accented and CJK text.
schema = {"geometry": "Point", "properties": {"name": "str:40", "n": "int", "v": "float", "ok": "bool", "day": "date"}}
with fiona.open(os.path.join(OUT, "gdal_points.shp"), "w", driver="ESRI Shapefile", schema=schema, crs=wgs, encoding="UTF-8") as dst:
    dst.write({"geometry": {"type": "Point", "coordinates": (126.9780, 37.5665)}, "properties": {"name": "서울 City Hall", "n": 1, "v": 1.5, "ok": True, "day": "2024-02-29"}})
    dst.write({"geometry": {"type": "Point", "coordinates": (2.2945, 48.8584)}, "properties": {"name": "Café Eiffel – Paris", "n": -20, "v": 0.001, "ok": False, "day": "2000-01-01"}})
    dst.write({"geometry": {"type": "Point", "coordinates": (-0.1276, 51.5072)}, "properties": {"name": "北京 / London", "n": 300, "v": -12345.6789, "ok": True, "day": None}})

# Polygons: a hole, and a multipolygon with an island.
schema = {"geometry": "MultiPolygon", "properties": {"id": "int", "label": "str:20"}}
with fiona.open(os.path.join(OUT, "gdal_polygons.shp"), "w", driver="ESRI Shapefile", schema=schema, crs=wgs, encoding="UTF-8") as dst:
    shell = [(0, 0), (10, 0), (10, 10), (0, 10), (0, 0)]
    hole = [(2, 2), (4, 2), (4, 4), (2, 4), (2, 2)]
    dst.write({"geometry": {"type": "MultiPolygon", "coordinates": [[shell, hole]]}, "properties": {"id": 1, "label": "with hole"}})
    island = [(20, 20), (25, 20), (25, 25), (20, 25), (20, 20)]
    dst.write({"geometry": {"type": "MultiPolygon", "coordinates": [[shell], [island]]}, "properties": {"id": 2, "label": "two parts"}})

# 3D lines.
schema = {"geometry": "3D LineString", "properties": {"id": "int"}}
with fiona.open(os.path.join(OUT, "gdal_linesz.shp"), "w", driver="ESRI Shapefile", schema=schema, crs=wgs) as dst:
    dst.write({"geometry": {"type": "LineString", "coordinates": [(0, 0, 10), (1, 1, 20.5), (2, 0, 30)]}, "properties": {"id": 1}})

# UTM 33N points (written by GDAL with an ESRI .prj). The tests know the longitude / latitude of each.
tr = Transformer.from_crs(4326, 32633, always_xy=True)
schema = {"geometry": "Point", "properties": {"name": "str:20"}}
with fiona.open(os.path.join(OUT, "gdal_utm33.shp"), "w", driver="ESRI Shapefile", schema=schema, crs=CRS.from_epsg(32633)) as dst:
    for name, lon, lat in [("Vienna", 16.3738, 48.2082), ("Prague", 14.4378, 50.0755), ("Berlin", 13.4050, 52.5200)]:
        x, y = tr.transform(lon, lat)
        dst.write({"geometry": {"type": "Point", "coordinates": (x, y)}, "properties": {"name": name}})

for f in sorted(os.listdir(OUT)):
    if f.startswith("gdal_"):
        print(f, os.path.getsize(os.path.join(OUT, f)))
