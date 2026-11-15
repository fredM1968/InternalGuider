# Publier Internal Guider

Deux façons de diffuser le plugin, qui se combinent :

- **Installeur Windows** (`InternalGuider-Setup-<version>.exe`) : à envoyer directement aux testeurs.
- **Liste officielle des plugins de NINA** (onglet *Plugins → Disponibles*) : l'installation et les mises à jour se font alors depuis NINA, par un manifeste proposé au dépôt `isbeorn/nina.plugin.manifests`.

## 1. Préparer le projet (une seule fois)

Dans `Properties\AssemblyInfo.cs`, vérifiez ces lignes (le `Guid` ne doit **jamais** changer d'une version à l'autre : c'est l'identifiant du plugin dans NINA) :

```csharp
[assembly: Guid("votre-guid-habituel")]
[assembly: AssemblyTitle("Internal Guider")]
[assembly: AssemblyDescription("Autoguidage intégré à NINA, alternative à PHD2")]
[assembly: AssemblyCompany("fredM1968")]          // sert d'auteur dans le manifeste
[assembly: AssemblyProduct("Internal Guider")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.0")]
[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
[assembly: AssemblyMetadata("Repository", "https://github.com/fredM1968/nina-internal-guider")]
```

Pour la liste officielle, le code source doit être public (dépôt GitHub) et la licence doit être indiquée : ajoutez le fichier `LICENSE` au dépôt.

## 2. Pour chaque nouvelle version

1. Augmentez `AssemblyVersion` et `AssemblyFileVersion` (par exemple `0.2.0.0`), puis compilez en **Release**, NINA fermé.
2. Dans PowerShell, depuis le dossier du projet :

   ```powershell
   .\installer\Package-Release.ps1 -RepoUrl "https://github.com/fredM1968/nina-internal-guider" -Author "fredM1968"
   ```

   Le dossier `dist` contient alors l'archive `.zip`, le `manifest.json` et, si Inno Setup 6 est installé, l'installeur `.exe`.
3. Sur GitHub, créez une *release* dont l'étiquette est `v<version>` (par exemple `v0.2.0.0`) et déposez-y le `.zip` (et l'installeur `.exe` pour les testeurs).
4. Ouvrez dans un navigateur l'adresse `Installer.URL` du manifeste : elle doit télécharger l'archive. C'est ce lien que NINA utilisera.

## 3. Proposer le plugin à la liste officielle

1. Faites un *fork* de `https://github.com/isbeorn/nina.plugin.manifests`.
2. Copiez `dist\manifest.json` dans le fork, au chemin `manifests\<première lettre>\<nom du plugin>\<version de NINA>\<version du plugin>\manifest.json`, par exemple `manifests\i\Internal Guider\3.0.0\0.2.0.0\manifest.json` (les dossiers de version sont facultatifs si vous ne gérez qu'une version).
3. Vérifiez le manifeste : installez Node.js, lancez `npm install` puis `node gather.js` dans le fork. Votre manifeste doit être signalé comme valide.
4. Ouvrez une *pull request*. L'équipe de NINA relit le plugin, vous pouvez avoir des remarques à corriger.
5. Pour une première diffusion limitée, ajoutez `"Channel": "Beta"` au manifeste (option `-Channel Beta` du script) : le plugin n'apparaît alors que pour les utilisateurs de la voie bêta.

## 4. Mises à jour

Répétez l'étape 2, puis proposez le nouveau manifeste (nouveau dossier de version) par une *pull request*. Les réglages, calibrations et journaux des utilisateurs restent dans `%LOCALAPPDATA%\NINA\InternalGuider` et ne sont pas touchés.

## Points à connaître

- Un plugin compilé pour NINA 3.2 doit être recompilé pour une autre version majeure de NINA.
- `Installer.Checksum` est la somme SHA256 de l'archive. Si vous la modifiez après coup, recalculez-la : le script le fait à chaque exécution.
- Les champs `Version` et `MinimumApplicationVersion` du manifeste sont écrits en nombres. Si `node gather.js` les refuse, passez-les en texte (`"3"` au lieu de `3`).
