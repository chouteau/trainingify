# Trainingify — site statique

Le dossier `docs/` est la version publiable directement par GitHub Pages. Il ne nécessite ni .NET, ni serveur, ni étape de compilation :

- `index.html` contient la page ;
- `css/site.css` contient les styles ;
- `js/main.js` contient les interactions côté navigateur ;
- `assets/` contient les images et le logo.

## Publication GitHub Pages

Dans les paramètres du dépôt, ouvrir **Pages**, choisir **Deploy from a branch**, sélectionner la branche souhaitée et le dossier `/docs`, puis enregistrer. Le workflow GitHub Pages du dépôt permet aussi une publication automatique via l’option **GitHub Actions**.
