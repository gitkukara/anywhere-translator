"""Convert the bundled, path-only provider SVGs to WPF vector resources (no runtime SVG dependency)."""
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src/TranslatorAnywhere/Assets/Providers"
OUTPUT = ROOT / "src/TranslatorAnywhere/Views/ProviderIcons.xaml"
WPF = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
XAML = "http://schemas.microsoft.com/winfx/2006/xaml"
ET.register_namespace("", WPF)
ET.register_namespace("x", XAML)

def tag(name):
    return f"{{{WPF}}}{name}"

resources = ET.Element(tag("ResourceDictionary"))
for file in sorted(SOURCE.glob("*.svg")):
    svg = ET.parse(file).getroot()
    image = ET.SubElement(resources, tag("DrawingImage"), {f"{{{XAML}}}Key": "ProviderIcon." + file.stem})
    drawing = ET.SubElement(image, tag("DrawingImage.Drawing"))
    group = ET.SubElement(drawing, tag("DrawingGroup"))
    ET.SubElement(group, tag("GeometryDrawing"), {"Brush": "Transparent", "Geometry": "M 0,0 L 24,0 24,24 0,24 Z"})
    gradients = {item.attrib["id"]: item for item in svg.iter() if item.tag.endswith("}linearGradient")}
    for path in svg:
        if not path.tag.endswith("}path"):
            continue
        fill = path.attrib.get("fill", svg.attrib.get("fill", "currentColor"))
        if fill == "none":
            continue
        rule = "F0 " if path.attrib.get("fill-rule", svg.attrib.get("fill-rule")) == "evenodd" else "F1 "
        geometry = ET.SubElement(group, tag("GeometryDrawing"), {"Geometry": rule + path.attrib["d"]})
        if fill.startswith("url(#"):
            source = gradients[fill[5:-1]]
            brush_prop = ET.SubElement(geometry, tag("GeometryDrawing.Brush"))
            brush = ET.SubElement(brush_prop, tag("LinearGradientBrush"), {
                "MappingMode": "Absolute" if source.attrib.get("gradientUnits") == "userSpaceOnUse" else "RelativeToBoundingBox",
                "StartPoint": source.attrib.get("x1", "0") + "," + source.attrib.get("y1", "0"),
                "EndPoint": source.attrib.get("x2", "1") + "," + source.attrib.get("y2", "0"),
            })
            for stop in source:
                color = stop.attrib["stop-color"].lstrip("#")
                if len(color) == 3:
                    color = "".join(letter * 2 for letter in color)
                alpha = round(float(stop.attrib.get("stop-opacity", "1")) * 255)
                ET.SubElement(brush, tag("GradientStop"), {"Color": f"#{alpha:02X}" + color, "Offset": stop.attrib.get("offset", "0")})
        else:
            geometry.set("Brush", "{DynamicResource InkBrush}" if fill == "currentColor" else fill)
ET.indent(resources, space="    ")
OUTPUT.write_text(ET.tostring(resources, encoding="unicode") + "\n", encoding="utf-8")
print(f"Converted {len(resources)} provider icons.")
