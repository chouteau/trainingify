# Consignes du dépôt

## Base de données SQLite

- Toute modification du schéma de la base SQLite doit obligatoirement être réalisée au moyen d'une migration Entity Framework Core.
- Ne pas modifier manuellement la structure de la base et ne pas utiliser `EnsureCreated` pour appliquer une évolution de schéma.
- La migration correspondante doit être ajoutée au dépôt avec le changement de modèle qui la nécessite.
- Vérifier que les migrations s'appliquent correctement sur une base existante avant de considérer le changement comme terminé.
