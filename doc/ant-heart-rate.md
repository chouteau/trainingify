# Cardio ANT+ sur Windows

La recherche des capteurs prend en charge le cardio ANT+ avec une clé ANT USB Stick 2
(VID 0FCF, PID 1008) ou ANTUSB-m (PID 1009), via LibUsbDotNet 2.2.85 et un pilote
USB compatible. Le pilote libusb-win32 déjà installé sur la machine de développement
a été testé sans remplacement du pilote. La bibliothèque native libusb0 fournie
par ce pilote doit être disponible pour l'architecture de l'application.

Activer la diffusion cardio de la montre, puis rechercher les capteurs dans les
paramètres. Choisir « Cardio ANT+ <numéro> » et activer sa connexion. La recherche
Bluetooth dure 5 secondes ; le canal ANT+ continue à écouter ensuite. Un canal
wildcard détecte le premier cardio à portée. Pour éviter de sélectionner le mauvais
capteur, ne laisser diffuser que celui voulu pendant cette recherche. Une connexion
enregistrée utilise ensuite son numéro précis, conservé sous la forme ANT:<numéro>
dans le champ Address existant. Aucun changement de schéma SQLite.

Les mesures sont réelles, indépendantes de l'ERG et du mode Col. Après cinq secondes
sans données, la mesure est remise à zéro et le statut signale l'attente du signal.
Le canal continue à rechercher le capteur. Après une erreur USB ou un retrait de
la clé, la réception retente automatiquement l'ouverture toutes les cinq secondes.
Les données USB en attente sont vidées à la réouverture, et le canal radio est fermé
à l'arrêt pour permettre les redémarrages sans débrancher la clé.
Fermer les autres logiciels réservant la même clé USB.

Ce support concerne uniquement le profil cardio ANT+ (type 120), pas les home
trainers ANT+ FE-C ni les autres profils. Les connexions Bluetooth restent disponibles.

## Vérification

`dotnet run --project tests/Trainingify.Ant.Tests`

Teste les trames, leur checksum, les paquets USB fragmentés ou regroupés et les
identifiants persistés. Ajouter `-- --hardware` pour ouvrir la clé et écouter
15 secondes ; fermer Trainingify avant ce test pour libérer la clé.

Références d'implémentation :
- https://github.com/Tigge/openant/blob/master/examples/raw_device_data.py
- https://github.com/LibUsbDotNet/LibUsbDotNet/tree/v2
