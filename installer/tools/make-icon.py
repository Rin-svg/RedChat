#!/usr/bin/env python3
"""Génère src/RedChat.Wpf/Assets/redchat.ico à partir de l'emblème vectoriel de MainWindow.xaml.

POURQUOI : l'icône de l'exécutable, du raccourci et du programme de désinstallation reprend EXACTEMENT les trois
tracés Bézier de l'emblème de l'application (même géométrie, mêmes couleurs), sur le fond bleu nuit du thème.
Aucun fichier externe : un changement de logo se fait ici puis `python3 installer/tools/make-icon.py`.
Dépendance : Pillow (pip install pillow).
"""
import os
from PIL import Image, ImageDraw

# Tracés copiés de src/RedChat.Wpf/MainWindow.xaml (en-tête). Forme : liste de segments « M / C / Z ».
PATHS = [
    ("#F21D45", "M 7,23 C 10,12 20,5 32,6 C 39,7 44,12 46,18 C 39,16 31,17 24,20 C 17,23 12,25 7,23 Z"),
    ("#FF7890", "M 4,24 C 12,25 19,22 26,19 C 34,16 42,16 49,19 C 45,24 35,27 25,28 C 15,29 7,28 4,24 Z"),
    ("#A90E2C", "M 8,29 C 19,31 35,29 45,25 C 41,31 27,34 15,32 C 12,32 9,31 8,29 Z"),
]
BACKGROUND_TOP, BACKGROUND_BOTTOM = (16, 29, 48), (10, 19, 33)   # dégradé du thème (#101D30 → #0A1321)
SIZES = [16, 24, 32, 48, 64, 128, 256]
EMBLEM_CENTER = (26.5, 19.5)   # centre de la boîte englobante des tracés
EMBLEM_WIDTH = 46.0            # largeur occupée par l'emblème (x de 4 à 49 + marge)


def cubic(p0, p1, p2, p3, steps=40):
    """Points d'une courbe de Bézier cubique (échantillonnage uniforme en t)."""
    pts = []
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        x = u**3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t**3 * p3[0]
        y = u**3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t**3 * p3[1]
        pts.append((x, y))
    return pts


def polygon(path_data):
    tokens = path_data.replace(",", " ").split()
    pts, i, current = [], 0, (0.0, 0.0)
    while i < len(tokens):
        cmd = tokens[i]
        if cmd == "M":
            current = (float(tokens[i + 1]), float(tokens[i + 2]))
            pts.append(current)
            i += 3
        elif cmd == "C":
            p1 = (float(tokens[i + 1]), float(tokens[i + 2]))
            p2 = (float(tokens[i + 3]), float(tokens[i + 4]))
            p3 = (float(tokens[i + 5]), float(tokens[i + 6]))
            pts.extend(cubic(current, p1, p2, p3)[1:])
            current = p3
            i += 7
        elif cmd == "Z":
            i += 1
        else:
            raise ValueError(f"Commande de tracé non gérée : {cmd}")
    return pts


def render(size):
    """Dessine à 8× la taille cible puis réduit (anti-crénelage de qualité)."""
    scale = 8
    big = size * scale
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    # fond : carré arrondi avec dégradé vertical
    gradient = Image.new("RGBA", (big, big))
    gd = ImageDraw.Draw(gradient)
    for y in range(big):
        k = y / (big - 1)
        gd.line([(0, y), (big, y)], fill=tuple(int(a + (b - a) * k) for a, b in zip(BACKGROUND_TOP, BACKGROUND_BOTTOM)) + (255,))
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, big - 1, big - 1], radius=int(big * 0.22), fill=255)
    image.paste(gradient, (0, 0), mask)

    # emblème centré, occupant ~72 % de la largeur
    k = (big * 0.72) / EMBLEM_WIDTH
    draw = ImageDraw.Draw(image)
    for color, data in PATHS:
        pts = [((x - EMBLEM_CENTER[0]) * k + big / 2, (y - EMBLEM_CENTER[1]) * k + big / 2) for x, y in polygon(data)]
        draw.polygon(pts, fill=color)
    return image.resize((size, size), Image.LANCZOS)


def main():
    target = os.path.join(os.path.dirname(__file__), "..", "..", "src", "RedChat.Wpf", "Assets", "redchat.ico")
    target = os.path.normpath(target)
    images = [render(s) for s in SIZES]
    images[-1].save(target, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    print("écrit :", target, os.path.getsize(target), "octets")


if __name__ == "__main__":
    main()
