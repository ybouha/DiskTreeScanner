# DiskTreeScanner

Analyseur d'espace disque pour Windows, en C# 12 / .NET 8 / WinForms (x64).

Sur un volume NTFS, avec les droits administrateur, DiskTreeScanner lit directement la table MFT au lieu de parcourir les dossiers un par un. Un moteur de repli, qui énumère les dossiers, couvre tous les autres cas : pas de droits administrateur, ReFS, exFAT, partages réseau, distributions WSL. Les deux moteurs remplissent le même modèle, et l'interface ne fait aucune différence entre eux.

## Résultats en bref

Machine de mesure :

- Portable AMD Ryzen 7 PRO 4750U (8 cœurs, 16 threads), 21,7 Go de RAM utilisable.
- SSD Samsung 970 EVO Plus 1 To (NVMe).
- Windows 11 (10.0.26300), .NET 8.0.33, mode d'alimentation Équilibré.
- C: : NTFS, 335 Go, environ 2,0 M éléments.

| Objectif du cahier des charges | Cible | Mesuré |
|---|---|---|
| Scan de C:\ (1 995 400 éléments), administrateur, moteur MFT, de bout en bout | ≤ 5 s | **2,2 à 2,45 s** (moyenne 2,29 s sur 3 scans). |
| Pic mémoire pour 2 M éléments | < 200 Mo | **193 Mo** de working set (178 Mo privés), dont 24 Mo de base du processus. |
| Clic ou dépliage dans l'arbre | < 16 ms | Clic : **6,3 ms** en moyenne, 10,7 ms au pire. Dépliage : **3,4 à 13,1 ms** ; 13,1 ms pour WinSxS et ses 28 283 enfants. Repaint compris, fenêtre de 1440 × 900. |

## Contenu de la solution

| Projet | Rôle |
|---|---|
| `src/DiskTreeScanner.Core` | Modèle de données, construction de l'arbre, agrégation, moteur de repli, analyses (gros fichiers, recherche, treemap, export CSV). Aucune dépendance à l'interface : le Core peut servir à un autre shell. |
| `src/DiskTreeScanner.Ntfs` | Lecture brute de la MFT (P/Invoke `LibraryImport`, code `unsafe`), parseur de FILE records, rafraîchissement par le journal USN. |
| `src/DiskTreeScanner.App` | Application WinForms `DiskTreeScanner.exe`, conforme à `../mockup/FolderSizes-maquette.html`. |
| `tools/DiskTreeScanner.Cli` | Scan en ligne de commande : temps par phase, totaux, pic mémoire, test du journal USN. |
| `tests/DiskTreeScanner.Tests` | 68 tests xUnit : records synthétiques, robustesse, filtres, cohérence des deux moteurs sur un vrai dossier. |
| `bench/DiskTreeScanner.Bench` | BenchmarkDotNet : parsing des records, construction de l'arbre, agrégation. |

## Compiler, tester, publier

Il faut le SDK .NET 8 (ou plus récent) pour compiler.

La publication de l'application produit **un seul fichier**, `publish\app\DiskTreeScanner.exe`, d'environ 1,3 Mo. Il est précompilé (ReadyToRun) et ses symboles de débogage sont intégrés. Il s'exécute sur le **runtime .NET 8 Desktop**, à installer une fois sur le poste. Pour un exécutable autonome, sans rien à installer, on ajoute `-p:SelfContained=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false`, et l'exe fait alors environ 63 Mo. Le trimming, qui réduirait encore la taille, n'est pas pris en charge par WinForms (erreur NETSDK1175).

```powershell
dotnet build DiskTreeScanner.sln -c Release
dotnet test tests\DiskTreeScanner.Tests -c Release                  # console administrateur : inclut les tests sur la vraie MFT
dotnet publish src\DiskTreeScanner.App -c Release -o publish\app    # un seul DiskTreeScanner.exe, win-x64, ReadyToRun
dotnet publish tools\DiskTreeScanner.Cli -c Release -o publish\cli
dotnet run -c Release --project bench\DiskTreeScanner.Bench -- --filter *
```

Sans droits administrateur, le test qui compare les deux moteurs sur la vraie MFT est ignoré (`Skip`). Tous les autres passent.

## Utilisation

### Application

Lancez `publish\app\DiskTreeScanner.exe`. Le manifeste est en `asInvoker` :

- **En administrateur**, sur un volume NTFS, le scan lit la MFT.
- **Sans droits administrateur**, une bannière propose « Restart as administrator », qui relance l'application via `runas`. En attendant, le scan passe par le moteur de repli.

Fonctions principales :

- **Barre d'outils** : combo de lecteur (lecteurs, distributions `\\wsl$\…`, « Browse folder… »), Scan, Stop, Refresh, recherche, filtre, ouverture dans l'Explorateur, export du rapport, unité de taille Auto / MB / GB.
- **Recherche** (Ctrl+F) : motifs avec jokers, plusieurs motifs séparés par `;` ou `,` (par exemple `*.vhdx; *.iso`). Le filtre restreint l'arbre aux résultats et recalcule les totaux.
- **Grille arbre** : tri par clic sur un en-tête (par défaut, Allocated décroissant). Les enfants d'un dossier sont triés à son premier dépliage, puis mis en cache.
- **Colonnes filtrables** : chaque en-tête porte un entonnoir, plein quand un filtre est actif. Un clic ouvre un panneau avec des préréglages.
  - Nom : motif.
  - Taille et Alloué : minimum et/ou maximum, par exemple `100 Mo` ou `1,5 Go`.
  - Modifié : intervalle de dates, ou 7 jours, 30 jours, un an, plus d'un an.
  - % du parent, Éléments, Fichiers et Dossiers : minimum et/ou maximum.

  Nom, taille, allocation et date filtrent les fichiers, et les totaux des dossiers sont recalculés sur les fichiers retenus. « Largest files » applique aussi ces critères. Le pourcentage et les compteurs masquent les lignes hors bornes. La légende au-dessus de l'arbre résume les filtres actifs.
- **Colonnes redimensionnables**, dans l'arbre et dans « Largest files » : on glisse le séparateur d'en-tête, et un double-clic rend sa largeur par défaut à la colonne. Les largeurs sont mémorisées.
- **Menu contextuel** :
  - un en-tête bleu acier avec le nom, l'emplacement, la taille et une pastille de type ;
  - Ouvrir, Ouvrir dans l'Explorateur, Propriétés (Alt+Entrée) ;
  - un sous-menu Copier (chemin complet, nom, taille, dossier parent) ;
  - Filtrer sur l'extension, Effacer les filtres ;
  - Entrer dans le dossier, Remonter, Exporter.

  Les raccourcis sont affichés à droite. Il n'y a pas de suppression en v1.
- **Panneaux du bas** : « Largest files » de la sélection, avec pastille de catégorie, et treemap du dossier sélectionné.
- **Options** : « Include NTFS special files », flux nommés (ADS), mode de comptage des hard links, « Force directory scan ». Les options s'appliquent au scan suivant.
- **Langue** EN/FR, depuis le sélecteur de la barre de titre.

Raccourcis : F5 analyser · Échap arrêter / effacer la recherche · Ctrl+R actualiser · Ctrl+F rechercher · Ctrl+E export CSV · flèches pour se déplacer · Droite/Gauche déplier/replier · Entrée entrer · Retour arrière remonter · Ctrl+C copier le chemin · Alt+Entrée propriétés · Maj+F10 menu contextuel.

Fichiers locaux :

- Préférences : `%LOCALAPPDATA%\DiskTreeScanner\settings.json`.
- Journal d'activité : `%LOCALAPPDATA%\DiskTreeScanner\logs\activity-AAAAMMJJ.log`. Il est aussi consultable dans View > Activity log.

Aucune donnée ne quitte la machine : il n'y a ni télémétrie, ni accès réseau, hormis les partages que vous analysez.

Au premier lancement, la fenêtre s'ouvre à 960 × 600 pixels logiques (DIP), sur l'écran où se trouve le curseur. Les lancements suivants reprennent la taille, la position et l'état agrandi de la dernière session.

La fenêtre est redimensionnable et « Per-Monitor V2 DPI aware » : polices, icônes, colonnes et marges suivent le DPI de chaque écran, y compris quand on la déplace d'un écran à l'autre. En dessous de 760 DIP de haut, la mise en page devient compacte : plus de titre de page ni de ligne de pied, tuiles plus basses. Le panneau du bas prend alors au plus 40 % de la place qu'il partage avec l'arbre. La mise en page de référence 1440 × 900 de la maquette a aussi été vérifiée.

Options de diagnostic de `DiskTreeScanner.exe`, pour l'automatisation :

| Option | Effet |
|---|---|
| `--screenshot FICHIER.png CHEMIN` | Scanne CHEMIN, enregistre une capture de la fenêtre, puis quitte. |
| `--ui-bench FICHIER.txt CHEMIN` | Scanne CHEMIN, chronomètre sélection, dépliage, défilement et peinture, puis quitte. |
| `--select CHEMIN-RELATIF` | Sélectionne et déplie ce dossier avant la capture. |
| `--size LxH` | Taille de la fenêtre en DIP. |
| `--lang en\|fr` | Langue de l'interface. |
| `--scale 1.5` | Simule un DPI de 150 %, pour vérifier les mises en page. |
| `--popup context\|Size\|Modified…` | Avec `--screenshot` : ouvre le menu contextuel de la sélection, ou le panneau de filtre d'une colonne, et l'incruste dans la capture. |
| `--filter Colonne:min:max` | Applique un filtre de colonne avant la capture, par exemple `Size:1GB:` ou `Modified:2026-01-01:`. Option répétable. |
| `--resize Colonne:dx` | Glisse le séparateur à gauche de la colonne de `dx` pixels, par le même code que la souris. Option répétable. |

Ces exécutions ne modifient pas les préférences.

### Ligne de commande

```text
DiskTreeScanner.Cli <chemin> [options]
DiskTreeScanner.Cli --usn-test [DOSSIER]

  --engine auto|mft|dir      moteur (auto : MFT en administrateur sur NTFS, sinon repli)
  --no-special               exclut les fichiers spéciaux NTFS ($MFT, $LogFile, $Extend…)
  --ads                      ajoute les flux nommés (ADS) à la taille des fichiers
  --hardlinks once|each|mixed
                             once : un fichier à plusieurs liens compté une fois (défaut)
                             each : compté sur chaque lien
                             mixed : taille sur chaque lien, allocation une seule fois
  --force-dir                équivaut à --engine dir (postes avec EDR)
  --top N, --depth N         gros fichiers et niveaux de dossiers affichés
  --csv FICHIER              export CSV de l'arbre
  --repeat N                 N scans : meilleur, moyen, pire
  --threads, --block-mb, --buffers, --queue, --dir-threads
                             réglages des moteurs (voir --help)
  --verbose                  affiche le journal d'activité
```

Exemples :

```powershell
DiskTreeScanner.Cli C:\ --repeat 3
DiskTreeScanner.Cli C:\ --hardlinks mixed
DiskTreeScanner.Cli C:\Users --engine dir
DiskTreeScanner.Cli --usn-test D:\
```

Le CLI affiche le temps de chaque phase : ouverture, lecture de la MFT, parsing, arbre, agrégation, premier rendu. Il affiche aussi les totaux, des statistiques (records, hard links, flux, orphelins) et la mémoire : working set et octets privés, au pic et au repos.

## Mesures détaillées

Toutes les mesures ont été prises sur la machine décrite plus haut, avec les binaires publiés (ReadyToRun).

### Scan de C:\ par le moteur MFT

| Phase | Durée | Détail |
|---|---|---|
| Ouverture | 77 à 94 ms | 18 extents de MFT, 2 985 984 records dont 1 952 300 en usage, table `$UpCase`. |
| Lecture de la MFT | 1,88 à 1,91 s | 2,2 Go en 881 blocs de 4 Mio, à 1,2 Go/s. 666 Mo de records libres ne sont pas lus. |
| Parsing | recouvert par la lecture | 0,84 à 0,96 s de CPU sur 15 threads ; il reste 2 à 8 ms de travail après le dernier bloc. |
| Arbre | 215 à 330 ms | Fusion des extensions, résolution des parents, classification, CSR et DFS, renumérotation en place. |
| Agrégation | 15 à 18 ms | |
| Premier rendu | 16 à 23 ms | Enfants de la racine triés, top 10 des fichiers. |
| **Total** | **2,2 à 2,45 s** | |

**Mémoire.** Le pic de working set est de 193 Mo, dont 178 Mo privés. Le tas managé fait 148 Mo, dont 54,5 Mo de noms. Avec `--repeat 3`, le pic sur l'ensemble des trois scans reste à 194 Mo.

**Débit de lecture.** Dans cette configuration, la lecture est limitée par le disque :

- 1,0 Go/s avec une seule lecture en vol ;
- 1,1 à 1,2 Go/s de 2 à 8 lectures en vol, avec des blocs de 4 à 16 Mio.

Au-delà du réglage par défaut, on ne gagne rien en vitesse et on perd en mémoire (jusqu'à 303 Mo de pic). Le parsing n'est pas le facteur limitant : il atteint 11 Go/s en benchmark.

### Moteur de repli

- **C:\ entier, en administrateur, avec « Force directory scan »** : 1 995 415 éléments, autant que le moteur MFT, en 21 à 37 s selon l'état du cache. La taille est inférieure d'environ 3 Go, car les fichiers de métadonnées NTFS (`$MFT`, `$LogFile`…) ne sont pas visibles par énumération. Le pic mémoire atteint 291 Mo.
- **Sans droits administrateur** (jeton utilisateur standard), C:\Program Files : 242 113 éléments en 2,6 s. Le journal indique « MFT engine skipped: not elevated », et la bannière « Restart as administrator » s'affiche. Les 20 dossiers protégés par leur ACL, dont WindowsApps, sont ignorés et signalés.
- **WSL**, `\\wsl$\docker-desktop` : 4 530 éléments en 0,7 s, sur 4 threads. `/proc`, `/run`, `/dev` et `/sys` sont exclus.

### Rafraîchissement par le journal USN (`--usn-test`)

Le test crée un dossier temporaire, le scanne, le modifie, rafraîchit par le journal, puis compare le résultat à un nouveau scan complet.

| Volume | Changement | Records | Durée | Traitement |
|---|---|---|---|---|
| D: (1,29 M éléments, calme) | Données d'un fichier | 1 | 31 à 43 ms | Ancêtres seulement |
| D: | Création, suppression, renommage, dossier imbriqué, hard link | 5 | 144 à 169 ms | Remise en page |
| C: (2,0 M éléments, actif) | Données d'un fichier, plus les écritures des autres programmes | 3 à 7 | 204 à 266 ms | Remise en page |
| C: | Création, suppression, renommage, dossier imbriqué, hard link | 5 à 7 | 185 à 242 ms | Remise en page |

Dans tous les cas, le dossier rafraîchi est identique au nouveau scan. Sur D:, le volume entier l'est aussi, à l'octet près.

### Interface (`--ui-bench` sur C:\, 2,0 M éléments)

Chaque mesure inclut un repaint synchrone de la grille.

| Mesure | 1440 × 900 | 960 × 600 |
|---|---|---|
| Sélection d'un dossier | 5,2 à 7,2 ms | 3,9 à 7,9 ms |
| Dépliage : tri des enfants et insertion des lignes | 3,4 à 13,1 ms | 2,4 à 11,1 ms |
| dont WinSxS (28 283 enfants) | 13,1 ms | 11,1 ms |
| Défilement d'une page (moyenne / pire) | 5,2 / 7,3 ms | 3,0 / 4,5 ms |
| Clic (moyenne / pire) | 6,3 / 10,7 ms | 5,4 / 7,5 ms |
| Peinture complète de la grille (moyenne / pire) | 3,4 / 4,7 ms | 2,0 / 2,3 ms |

### Benchmarks (BenchmarkDotNet 0.14, .NET 8.0.33)

| Benchmark | Moyenne | Remarque |
|---|---|---|
| Re-protection des records (comprise dans les deux lignes suivantes) | 33 ns par record | Remet l'USN en fin de chaque bloc de 512 octets, pour pouvoir parser à nouveau. |
| Parsing, 1 thread | 350 ns par record | Environ 2,9 Go/s. |
| Parsing, 15 threads | 90 ns par record | Environ 11 Go/s, dix fois le débit du disque. |
| Décodage d'une runlist de 64 runs | 634 ns | |
| Construction de l'arbre, 2 M nœuds | 209 ms | 16 Mo alloués (positions des parents et plages). |
| Remise à zéro des dossiers, 2 M nœuds | 4,7 ms | |
| Remise à zéro et agrégation parallèle, 2 M nœuds | 9,3 ms | |

Les données sont synthétiques et déterministes :

- **Records** : une MFT de 65 536 records (64 Mio), avec 12 % de dossiers, des noms de 4 à 40 caractères, des alias 8.3, des hard links, des ADS et des fichiers compressés ou sparse.
- **Arbre** : 2 M nœuds, profondeur en ln N, quelques dossiers géants, 5 % de records « réutilisés » placés avant leur parent, 64 orphelins.

Cet arbre est volontairement moins favorable que C:, où les enfants suivent souvent leur parent dans la MFT. La même construction y prend environ 75 ms.

## Architecture

### Moteur 1 : lecture de la MFT (NTFS, administrateur)

1. **Ouverture du volume.** Le moteur ouvre deux handles sur `\\.\C:` : l'un synchrone pour les FSCTL, l'autre en `FILE_FLAG_NO_BUFFERING | FILE_FLAG_OVERLAPPED` pour les lectures. L'ouverture brute est consignée dans le journal d'activité : un EDR ou Defender peuvent la surveiller, d'où l'option « Force directory scan ».
2. **Géométrie.** `FSCTL_GET_NTFS_VOLUME_DATA` donne les tailles de secteur, de cluster et de record, `MftStartLcn` et `MftValidDataLength`.
3. **Localisation de la MFT.**
   - Le moteur lit l'enregistrement 0, applique les fixups et décode la runlist de `$DATA`. Les deltas LCN sont signés, et un run sans offset est sparse.
   - Il suit `$ATTRIBUTE_LIST` si les extents de la MFT débordent dans des records d'extension.
   - `$MFT::$BITMAP` indique les records en usage. Les plages libres ne sont pas lues, et un index de rang donne à chaque record en usage un slot compact.
4. **Pipeline de lecture.** Un seul thread lit le disque. Il garde deux lectures overlapped en vol, dans un petit pool de buffers de 4 Mio alignés (`NativeMemory.AlignedAlloc`). Les blocs lus passent par un `Channel` borné à N = cœurs − 1 parseurs, qui rendent ensuite les buffers au pool. Le temps d'attente du lecteur est mesuré : 1 à 15 ms par scan en général, jusqu'à 200 ms sur certains, pour environ 1,9 s de lecture. Le parsing seul va dix fois plus vite que le disque (voir les benchmarks).
5. **Parsing.** Le parseur travaille en `Span<byte>`, sans allocation.
   - Il contrôle la signature, l'en-tête et le flag in-use. Les fixups sont obligatoires : un record incohérent est ignoré et compté dans `BadRecords`.
   - Chaque attribut est vérifié contre les bornes du record avant d'être lu.
   - `$STANDARD_INFORMATION` donne les attributs et la date de modification.
   - `$FILE_NAME` donne le nom et le parent. Un nom DOS seul est ignoré quand un nom long existe. Plusieurs noms longs signalent des hard links.
   - `$DATA` sans nom donne RealSize et AllocatedSize, ou TotalAllocated pour un flux compressé ou sparse. Le flux `WofCompressedData` donne l'allocation réelle d'un fichier compressé par WOF (CompactOS). Les autres flux nommés sont des ADS, comptés à part.
   - Les records d'extension sont fusionnés dans leur record de base après le parsing.
6. **Volume vivant.** Une référence parent dont la séquence ne correspond plus, un parent absent ou une boucle font rattacher l'élément au nœud `<orphans>`, dernier enfant de la racine. La construction de l'arbre et l'agrégation sont itératives : une boucle dans les références ne provoque ni récursion infinie ni crash.

### Modèle de données

Le modèle est un struct-of-arrays, sans aucun objet par fichier (`TreeModel`). On y trouve `Parent`, `SubtreeEnd`, `Size`, `Allocated`, `Flags` et `Modified`, plus le nom (offset et longueur) et le numéro de record.

- **Ordre préfixe DFS.** Les nœuds sont renumérotés en ordre préfixe : le sous-arbre de `i` est la plage `[i + 1, SubtreeEnd[i])`. Le premier enfant de `i` est `i + 1`, et le frère qui suit l'enfant `c` est `SubtreeEnd[c]`. Le filtrage par ancêtre, le top N des gros fichiers, la recherche et l'export sont donc des parcours linéaires de plage.
- **Construction** (`TreeBuilder`) en quatre étapes :
  1. classification : rattaché, exclu ou orphelin, avec coupure des boucles ;
  2. CSR : comptage par parent, puis somme préfixe ;
  3. DFS itératif ;
  4. calcul de `SubtreeEnd`.

  Dans le moteur MFT, les tableaux bruts sont ensuite permutés en place dans l'ordre DFS, par un gather parallèle. Il n'utilise qu'un tampon temporaire de 4 octets par nœud : aucune seconde copie des colonnes n'est allouée.
- **Agrégation** (`Aggregator`) : un parcours par positions décroissantes est un parcours postfixe. Les sous-arbres disjoints sont agrégés en parallèle, puis les quelques nœuds au-dessus séquentiellement.
- **Compteurs sans mémoire par nœud.** Le nombre d'éléments est la longueur de la plage. Le nombre de dossiers se compte avec un index de rang sur un bitmap « est un dossier », soit 0,19 octet par nœud au lieu d'un `int[]`.
- **Noms** (`NameArena`) : une arène append-only en blocs de 512 Kio. Les noms entièrement Latin-1, soit 99,998 % des 2 M noms de C:, sont stockés sur un octet par caractère, les autres en UTF-16 avec un bit marqueur.
- **Comparaisons insensibles à la casse** : elles utilisent la table `$UpCase` du volume, pour la recherche et le tri par nom.
- **Sous-dossier sélectionné** : la MFT est quand même lue en entier, puis la vue se limite à la plage DFS du dossier.

### Moteur 2 : repli

Le moteur de repli utilise `NtQueryDirectoryFile` avec `FileIdBothDirectoryInformation` et un buffer de 128 Kio. À défaut, il utilise `FindFirstFileExW` (`FindExInfoBasic`, `FIND_FIRST_EX_LARGE_FETCH`).

- **Parallélisme** : work-stealing par dossier, borné. Le moteur prend autant de threads que de cœurs, entre 4 et 32, en local ; 8 sur un partage réseau ; 4 sur WSL, car le serveur 9P ne gagne rien au-delà.
- **Taille allouée** : elle vient de `AllocationSize`. Les placeholders cloud (OneDrive, `RECALL_ON_DATA_ACCESS`) ont une allocation de 0.
- **Points de reparse** : les junctions, liens symboliques et points de montage (« name surrogates ») ne sont pas suivis, ce qui évite le double comptage. Les autres dossiers de reparse sont listés.
- **Sur NTFS**, les fichiers dont les données sont résidentes dans la MFT ont une allocation de 0, comme dans le moteur 1. Les hard links sont dédoublonnés par FileId. En administrateur, `SeBackupPrivilege` permet de lister les dossiers protégés par leur ACL.
- **À la racine d'une distribution WSL**, `/proc`, `/sys`, `/dev`, `/run`, `/mnt/<lettre>`, `/mnt/wsl` et `/mnt/wslg` sont exclus : ce sont des systèmes de fichiers virtuels ou les lecteurs Windows.

Le moteur est choisi automatiquement. La MFT est utilisée si la cible est sur un volume NTFS local, que l'application est en administrateur et que « Force directory scan » n'est pas coché. Dans tous les autres cas, et si la lecture de la MFT échoue, c'est le repli. La raison du choix est inscrite dans le journal.

### Rafraîchissement par journal USN

Après un scan MFT, le modèle mémorise `UsnJournalID` et `NextUsn`. Refresh lit ensuite les changements avec `FSCTL_READ_USN_JOURNAL`, puis relit chaque record modifié avec `FSCTL_GET_NTFS_FILE_RECORD`, à travers le même parseur que le scan.

- **Changements de données seulement** : les écarts de taille, d'allocation et de date sont propagés aux seuls ancêtres.
- **Créations, suppressions, renommages ou hard links** : l'arbre est remis en page à partir des nœuds existants et des records modifiés, sans relire la MFT. Un record supprimé puis réutilisé par NTFS pour un autre fichier devient un nouvel élément.
- **Scan complet** : le moteur relance un scan complet si le journal a été recréé ou a débordé, si plus de 100 000 records ont changé, ou si un record modifié a une liste d'attributs non résidente.

Le modèle affiché n'est jamais modifié en place : un nouveau modèle remplace l'ancien, et la sélection est conservée.

### Interface

L'interface est en WinForms pur, sans bibliothèque tierce.

- **Grille arbre** : contrôle owner-drawn, double-buffered et entièrement virtuel. Seules les lignes visibles sont peintes, à partir d'une liste aplatie `int[]` des nœuds dépliés. Le texte passe par `ExtTextOutW` (GDI), avec des polices et des icônes mises en cache pour chaque DPI.
- **Treemap** : algorithme squarified, rendu GDI+ dans un bitmap en cache, recalculé à la sélection ou au redimensionnement.
- **Largest files** : calculé en arrière-plan avec un tas sur la plage DFS de la sélection.
- **Chrome personnalisé** : barre de titre sombre `#1E2A38` avec menus et sélecteur EN/FR, gérée par `WM_NCCALCSIZE` et `WM_NCHITTEST`. Le déplacement par la barre de titre, le redimensionnement par les bords et la bordure DWM sont conservés.
- **Rien de bloquant sur le thread UI** : scan, rafraîchissement, recherche, gros fichiers et export s'exécutent en tâche de fond, annulables par Stop (`CancellationToken`).

## Comptage

Il faut distinguer trois grandeurs : la taille logique (Size), la taille allouée (Allocated), et l'occupation réelle quand des clusters sont partagés entre hard links.

| Sujet | Règle |
|---|---|
| Hard links | Par défaut, la taille et l'allocation d'un fichier sont comptées une seule fois, sur son premier lien (premier `$FILE_NAME` du record). L'option `each` les compte sur chaque lien. L'option `mixed` compte la taille sur chaque lien et l'allocation une seule fois : l'occupation disque reste exacte. |
| Flux nommés (ADS) | Comptés à part, et ajoutés aux fichiers seulement avec l'option. |
| Fichiers spéciaux NTFS | `$MFT`, `$LogFile`, `$Bitmap`, `$Extend\…`, inclus par défaut (option). `$Secure:$SDS` et `$UsnJrnl:$J` comptent dans l'allocation de leur fichier. `$BadClus:$Bad`, qui cartographie tout le volume sans être marqué sparse, est ignoré. `hiberfil.sys` et `pagefile.sys` restent visibles comme des fichiers normaux. |
| Fichiers résidents | Les données sont dans le record MFT : allocation de 0, dans les deux moteurs. |
| Fichiers compressés, sparse, WOF | Allocation réelle : TotalAllocated ou flux WOF. |

## Écarts assumés par rapport au cahier des charges

- **Noms.** Le cahier des charges demande une arène `char[]` unique. DiskTreeScanner utilise une arène d'octets en blocs de 512 Kio, en Latin-1 quand c'est possible. Elle occupe 50 Mo au lieu de 100 Mo pour les 2 M noms de C:, et les blocs évitent la copie, qui doublerait le pic, à chaque agrandissement d'un tableau unique. C'est ce qui tient le pic sous 200 Mo.
- **`itemCount` / `folderCount`.** Ces tableaux ne sont pas stockés : les éléments sont la longueur de la plage DFS, et les dossiers sont comptés par rang sur un bitmap. Cela économise 16 Mo pour 2 M nœuds.
- **Lecture.** Il y a toujours un seul thread lecteur, mais il garde deux lectures overlapped en vol (option `--queue`), ce qui réduit les temps morts entre deux blocs : 1,0 Go/s avec une seule lecture, 1,1 à 1,2 Go/s avec deux. La taille de bloc est de 4 Mio par défaut ; on peut la régler de 4 à 16 Mio avec `--block-mb`.
- **Fenêtre.** Elle s'ouvre à 960 × 600 DIP au lieu de 1440 × 900. Elle reste redimensionnable, et la mise en page 1440 × 900 a été vérifiée.

## Limites connues

- Sur un volume système, d'autres programmes écrivent en permanence. Un Refresh y est donc presque toujours « structurel » : il reste incrémental (aucune relecture de la MFT), mais il remet l'arbre en page. Le chemin « ancêtres seulement » s'applique quand seules des données ont changé, typiquement sur un volume de données.
- Le moteur de repli est beaucoup plus lent sur un gros volume système (21 à 37 s pour C:\, contre 2,3 s), car il doit ouvrir chaque dossier. C'est le prix d'un fonctionnement sans droits administrateur.
- Il consomme aussi plus de mémoire : 291 Mo de pic pour les 2 M éléments de C:\. La cible de 200 Mo du cahier des charges porte sur le moteur MFT (section « Moteur 1 »). Le pic est atteint pendant la construction de l'arbre : 204 Mo à la fin de l'énumération, 291 Mo ensuite. À ce moment, les colonnes par blocs et les tableaux finaux en ordre DFS coexistent.
- Pendant un nouveau scan, le résultat précédent reste affiché, donc en mémoire, jusqu'à ce que le nouveau le remplace.
- Trié par nom, le premier dépliage d'un dossier géant dépasse la cible de 16 ms. WinSxS (28 283 noms longs au préfixe commun) prend environ 32 ms, contre 13 à 17 ms trié par taille. L'ordre est ensuite mis en cache.
- Les filtres de colonnes restreignent l'arbre et « Largest files », mais pas le treemap, qui montre toujours les tailles réelles.
- L'exécutable publié dépend du framework : le runtime .NET 8 Desktop doit être installé, sinon il faut la variante autonome d'environ 63 Mo (voir « Compiler, tester, publier »).
