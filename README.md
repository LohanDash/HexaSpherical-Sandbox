# HexaSpherical Sandbox

Premier prototype Godot 4 C# d'une planète procédurale sphérique pavée de cellules
hexagonales et de 12 pentagones.

## Lancer

Double-cliquer sur `Open HexaSpherical Sandbox.bat`, puis appuyer sur `F6`/`F5`.
Ce lanceur configure explicitement le SDK .NET pour éviter les problèmes de `PATH`
après une nouvelle installation. Il est également possible d'ouvrir `project.godot`
avec **Godot Engine (Mono)** après avoir redémarré la session Windows.

- ZQSD ou WASD : déplacement
- Souris : caméra FPS
- Espace : saut
- Échap : libérer/capturer la souris
- Clic gauche : casser le bloc supérieur visé
- Clic droit : poser un bloc sur la cellule visée
- F5 pendant la partie : basculer entre vue FPS et troisième personne

Les pentagones structurels sont colorés en violet afin de les rendre visibles dans
ce prototype. Le terrain est déterministe et dépend de `Seed` dans le nœud Planet.
