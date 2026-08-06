# Trainingify

Trainingify est une application d’entraînement indoor pour cyclistes. Le site vitrine est une page statique autonome, publiée depuis le dossier [`docs/`](docs/).

## Site statique

Le site WebSite ne dépend plus d’ASP.NET Core pour être servi. Ouvrir `docs/index.html` dans un navigateur suffit pour le consulter ; aucune installation ni compilation n’est requise. Les interactions du simulateur sont exécutées côté navigateur dans `docs/js/main.js`.

Pour GitHub Pages, sélectionner `/docs` comme dossier de publication dans les paramètres Pages, ou utiliser le workflow [`deploy-pages.yml`](.github/workflows/deploy-pages.yml) en sélectionnant **GitHub Actions** comme source.

## Getting Started

Pour développer l’application native, utiliser le projet `src/Trainingify/Trainingify.csproj`.

## Build and Test

Le site statique ne possède pas d’étape de build. Pour le tester localement avec un serveur HTTP :

```powershell
python -m http.server 8080 --directory docs
```

Puis ouvrir <http://localhost:8080>.

## Contribute

Les contributions sont les bienvenues. Garder le site publiable sans serveur et conserver les chemins relatifs depuis `docs/index.html`.
