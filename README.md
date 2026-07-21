# HexaSpherical Sandbox — Alpha 0.0.3

Premier prototype Godot 4 C# d'une planète procédurale sphérique pavée de cellules
hexagonales et de 12 pentagones.

La planète de l'Alpha 0.0.3 possède un rayon de 36 mètres et environ 10 000
cellules, générées progressivement chunk par chunk.

Le terrain utilise un streaming sphérique autour du joueur : les 12 mètres les
plus proches affichent les voxels et grottes complets, la zone allant jusqu'à 26
mètres utilise une surface simplifiée, et les chunks plus éloignés sont déchargés.

Le sous-sol contient des chambres et tunnels interconnectés. De rares puits
naturels peuvent relier directement la surface au réseau de grottes.

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
- G : allumer ou éteindre la lampe torche directionnelle
- Double Espace : activer ou désactiver le vol créatif
- Espace / Maj en vol : monter / descendre

Un cycle jour/nuit complet dure cinq minutes par défaut. L'heure locale dépend
également de la position du joueur sur la planète : voyager autour du globe fait
donc naturellement avancer ou reculer le soleil dans le ciel.

## Musique dynamique

La surface alterne Aria Math et Mice on Venus selon une rotation probabiliste.
Entrer dans une grotte interrompt la musique de surface. Après un délai de 5 à 10
secondes, un tirage de 1 % est effectué chaque seconde pour lancer 13. Une fois
déclenché, il ne peut plus revenir avant une sortie puis une nouvelle entrée.
Dire Dire Docks, Far, Chirp, Mellohi et Stal sont enregistrés pour les futurs
contextes océan, espace, mort survie, mort hardcore et jukebox.

Les MP3 placés dans `Music/` sont des ressources locales optionnelles et ne sont
pas distribués dans le dépôt public. Le jeu reste fonctionnel lorsqu'ils manquent.

Les pentagones structurels sont colorés en violet afin de les rendre visibles dans
ce prototype. Le terrain est déterministe et dépend de `Seed` dans le nœud Planet.
