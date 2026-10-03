// corrigé le 01-01-2019
// =====================================================================================
// IFS : Improved F-Score Calculation Program (EEIC'2019, University of Bejaia)
// Méthode de référence : L'haddad, S., & Kemmouche, A. Hyperspectral Feature Selection
//                        using Improved F-Score Technique (2019).
//
// Le programme calcule, pour chaque bande spectrale B_i d'une image multibande, son
// Improved F-Score F_i à partir d'une image classée (image d'étiquettes) produite par
// n'importe quel classifieur :
//
//                         somme_j ( moy_ij - moy_i )^2
//   F_i = -----------------------------------------------------------------
//          somme_j [ 1/(n_j - 1) * somme_k ( x_kij - moy_ij )^2 ]
//
//   moy_i  : moyenne radiométrique de la bande i (tous les pixels des classes retenues)
//   moy_ij : moyenne radiométrique de la bande i sur les pixels de la classe j
//   x_kij  : valeur radiométrique du k-ième pixel de la classe j dans la bande i
//   n_j    : nombre de pixels de la classe j
//
// Plus F_i est grand, plus la bande i sépare bien les classes. Les scores sont ensuite
// normalisés (w_i = F_i / somme_k F_k) : chaque score est compris entre 0 et 1 et leur
// somme vaut 1. Ils sont enregistrés dans un fichier .txt du dossier « Scores of Bands »,
// juste sous le dossier du projet.
//
// Entrées acceptées :
//   - image multibande : soit un seul fichier ENVI (.hdr + fichier de données, entrelacement
//     BSQ, BIL ou BIP, types 1, 2, 3, 4, 5, 12, 13, 14, 15), soit un ensemble de fichiers
//     image (une bande par fichier ; TIFF, PNG, BMP, GIF, JPEG ; 8, 16 bits ou flottant ;
//     un TIFF multipage fournit une bande par page) ;
//   - image classée : TIFF, PNG, BMP, GIF, JPEG ou ENVI (.hdr). Chaque couleur (ou valeur)
//     distincte de l'image classée est une classe.
// =====================================================================================
using Microsoft.Win32;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IFS
{
    public partial class MainWindow : Window
    {
        // ---- Données choisies par l'utilisateur ----
        // Image multibande : soit un seul fichier ENVI .hdr, soit les fichiers des bandes
        private List<string> fichiersMultibande = new List<string>();
        // Image classée (image d'étiquettes)
        private string fichierEtiquettes = null;

        public MainWindow()
        {
            InitializeComponent();
        }

        // =================================================================================
        // ÉTAPE 1 : SÉLECTION DE L'IMAGE MULTIBANDE
        // L'utilisateur choisit soit UN fichier .hdr (ENVI) contenant toutes les bandes,
        // soit l'ensemble des fichiers image des bandes (sélection multiple).
        // =================================================================================
        private void buttonMultibande_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFile = new OpenFileDialog();
            openFile.Title = "Select a multiband image (.hdr) or the set of band image files";
            openFile.Multiselect = true;
            openFile.Filter = "Multiband image (.hdr) or band images|*.hdr;*.tif;*.tiff;*.png;*.bmp;*.gif;*.jpg;*.jpeg|"
                            + "ENVI multiband image (*.hdr)|*.hdr|"
                            + "Band images (*.tif;*.tiff;*.png;*.bmp;*.gif;*.jpg;*.jpeg)|*.tif;*.tiff;*.png;*.bmp;*.gif;*.jpg;*.jpeg|"
                            + "All files (*.*)|*.*";
            if (openFile.ShowDialog(this) != true || openFile.FileNames.Length == 0)
                return;

            List<string> selection = new List<string>(openFile.FileNames);

            // Un fichier .hdr contient déjà toutes les bandes : il doit être sélectionné seul
            int nbHdr = selection.FindAll(f => EstFichierEnvi(f)).Count;
            if (nbHdr > 0 && selection.Count > 1)
            {
                MessageBox.Show("Select either a single .hdr file containing all the bands, or the set of band image files, but not both.",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Les bandes sont numérotées dans l'ordre des noms de fichiers (ordre de l'Explorateur :
            // B1, B2, ..., B10), car l'ordre de la boîte de dialogue n'est pas garanti.
            selection.Sort(ComparerNomsFichiers);
            fichiersMultibande = selection;

            if (selection.Count == 1)
                textBoxMultibande.Text = selection[0];
            else
                textBoxMultibande.Text = selection.Count + " band files : " + string.Join(" ; ", selection.ConvertAll(f => Path.GetFileName(f)));
            textBoxResultat.Text = "";
        }

        // =================================================================================
        // ÉTAPE 2 : SÉLECTION DE L'IMAGE CLASSÉE (IMAGE D'ÉTIQUETTES)
        // =================================================================================
        private void buttonEtiquettes_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFile = new OpenFileDialog();
            openFile.Title = "Select label image (classified image)";
            openFile.Multiselect = false;
            openFile.Filter = "Label image (*.tif;*.tiff;*.png;*.bmp;*.gif;*.jpg;*.jpeg;*.hdr)|*.tif;*.tiff;*.png;*.bmp;*.gif;*.jpg;*.jpeg;*.hdr|"
                            + "All files (*.*)|*.*";
            if (openFile.ShowDialog(this) != true)
                return;

            fichierEtiquettes = openFile.FileName;
            textBoxEtiquettes.Text = fichierEtiquettes;
            textBoxResultat.Text = "";

            // La compression JPEG modifie les couleurs et crée de fausses classes
            string ext = Path.GetExtension(fichierEtiquettes).ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg")
                MessageBox.Show("Warning: JPEG compression alters the colours of a classified image and creates false classes. "
                              + "Prefer a PNG, TIFF, BMP or ENVI (.hdr) label image.",
                                "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // =================================================================================
        // ÉTAPE 3 : EXÉCUTION
        // Calcul des Improved F-Scores, normalisation, enregistrement dans « Scores of Bands »
        // puis affichage. Le calcul est fait sur un thread séparé pour que la fenêtre reste
        // réactive avec les grandes images hyperspectrales.
        // =================================================================================
        private async void buttonExecuter_Click(object sender, RoutedEventArgs e)
        {
            if (fichiersMultibande.Count == 0)
            {
                MessageBox.Show("Select the multiband image first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (string.IsNullOrEmpty(fichierEtiquettes))
            {
                MessageBox.Show("Select the label image (classified image) first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            buttonExecuter.IsEnabled = false;
            textBoxResultat.Text = "Computing the Improved F-Scores, please wait...";
            List<string> bandes = new List<string>(fichiersMultibande);
            string etiquettes = fichierEtiquettes;
            string dossier = DossierScores();

            try
            {
                ResultatIFS r = await ExecuterSurThreadSTA(() =>
                {
                    ResultatIFS res = CalculerScores(bandes, etiquettes);
                    res.FichierScores = EcrireFichierScores(dossier, res);
                    return res;
                });

                textBoxResultat.Text = TexteScores(r) + Environment.NewLine + "File : " + r.FichierScores;
                MessageBox.Show("The scores (weights) obtained for the bands of the multiband image are stored in the \"Scores of Bands\" file."
                              + Environment.NewLine + Environment.NewLine + r.FichierScores,
                                "Improved F-Score Calculation", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                textBoxResultat.Text = "";
                MessageBox.Show("Error: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                buttonExecuter.IsEnabled = true;
            }
        }

        // Exécute le calcul sur un thread STA séparé (la lecture des images par WPF l'exige)
        private static Task<T> ExecuterSurThreadSTA<T>(Func<T> calcul)
        {
            TaskCompletionSource<T> tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread t = new Thread(() =>
            {
                try { tcs.SetResult(calcul()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            return tcs.Task;
        }

        // =================================================================================
        // CALCUL COMPLET : lecture de l'image classée, des bandes, calcul des IFS, normalisation
        // =================================================================================
        public static ResultatIFS CalculerScores(List<string> fichiersMultibande, string fichierEtiquettes)
        {
            ResultatIFS r = new ResultatIFS();
            r.FichierMultibande = fichiersMultibande.Count == 1
                ? fichiersMultibande[0]
                : Path.GetDirectoryName(Path.GetFullPath(fichiersMultibande[0])) + "  (" + fichiersMultibande.Count + " band files)";
            r.FichierEtiquettes = fichierEtiquettes;
            r.NomBase = Path.GetFileNameWithoutExtension(fichiersMultibande[0]);

            // 1. Lecture de l'image classée : une étiquette (couleur ou valeur) par pixel
            int W, H;
            bool couleur;
            double[] etiquettes = LireEtiquettes(fichierEtiquettes, out W, out H, out couleur);
            r.Largeur = W;
            r.Hauteur = H;
            r.EtiquettesCouleur = couleur;

            // 2. Recherche des classes (couleurs distinctes) et de leur nombre de pixels n_j
            List<double> couleurClasses;
            List<int> nbrCouleur;
            int[] classeDuPixel = IndexerClasses(etiquettes, out couleurClasses, out nbrCouleur);
            r.ValeursClasses = couleurClasses;
            r.PixelsParClasse = nbrCouleur;

            // La variance d'une classe exige n_j >= 2 (division par n_j - 1) :
            // une classe d'un seul pixel est ignorée.
            bool[] retenue = new bool[couleurClasses.Count];
            for (int j = 0; j < couleurClasses.Count; j++)
            {
                retenue[j] = nbrCouleur[j] >= 2;
                if (retenue[j]) { r.ClassesRetenues++; r.PixelsUtilises += nbrCouleur[j]; }
                else r.ClassesIgnorees++;
            }
            if (r.ClassesRetenues < 2)
                throw new InvalidOperationException("The label image must contain at least 2 classes of at least 2 pixels each (classes found: "
                                                    + couleurClasses.Count + ").");

            // 3. Liste des bandes de l'image multibande (fichier ENVI ou fichiers image)
            List<SourceBande> sources = PreparerBandes(fichiersMultibande);
            if (sources.Count == 0)
                throw new InvalidOperationException("No band was found in the multiband image.");

            // 4. Calcul de l'Improved F-Score de chaque bande (une bande en mémoire à la fois)
            for (int b = 0; b < sources.Count; b++)
            {
                int w, h;
                double[] bande = LireBande(sources[b], out w, out h);
                if (w != W || h != H)
                    throw new InvalidDataException("Band " + (b + 1) + " (" + sources[b].Nom + ") is " + w + " x " + h
                                                   + " pixels while the label image is " + W + " x " + H + " pixels.");
                for (int p = 0; p < bande.Length; p++)
                    if (retenue[classeDuPixel[p]] && !double.IsFinite(bande[p]))
                        throw new InvalidDataException("Band " + (b + 1) + " (" + sources[b].Nom + ") contains invalid values (NaN or infinity).");

                r.NomsBandes.Add(sources[b].Nom);
                r.ScoresBruts.Add(CalculerIFS(bande, classeDuPixel, nbrCouleur, retenue));
            }

            // 5. Normalisation : chaque score entre 0 et 1, somme des scores = 1
            r.Scores = NormaliserScores(r.ScoresBruts);
            return r;
        }

        // =================================================================================
        // IMPROVED F-SCORE D'UNE BANDE
        //   bande         : valeurs radiométriques de la bande i (pixel p = y * largeur + x)
        //   classeDuPixel : numéro de classe j de chaque pixel
        //   nbrCouleur    : nombre de pixels n_j de chaque classe
        //   retenue       : classes prises en compte (n_j >= 2)
        // =================================================================================
        public static double CalculerIFS(double[] bande, int[] classeDuPixel, List<int> nbrCouleur, bool[] retenue)
        {
            int nbClasses = nbrCouleur.Count;

            // (a) Moyenne radiométrique de la bande : avg(x_i) = Xi
            double Xi = 0;
            long n = 0;
            for (int p = 0; p < bande.Length; p++)
            {
                if (retenue[classeDuPixel[p]])
                {
                    Xi += bande[p];
                    n++;
                }
            }
            Xi /= n;

            // (b) Moyenne radiométrique de la bande sur chaque classe : avg(x_ij) = moy[j]
            double[] moy = new double[nbClasses];
            for (int p = 0; p < bande.Length; p++)
            {
                int j = classeDuPixel[p];
                if (retenue[j]) moy[j] += bande[p];
            }
            for (int j = 0; j < nbClasses; j++)
                if (retenue[j]) moy[j] /= nbrCouleur[j];

            // (c) Numérateur : dispersion inter-classes  somme_j (avg(x_ij) - avg(x_i))^2
            double num = 0;
            for (int j = 0; j < nbClasses; j++)
                if (retenue[j]) num += (moy[j] - Xi) * (moy[j] - Xi);

            // (d) Dénominateur : dispersion intra-classe
            //     den[j] = somme_k (x_kij - avg(x_ij))^2, puis variance den[j] / (n_j - 1)
            double[] den = new double[nbClasses];
            for (int p = 0; p < bande.Length; p++)
            {
                int j = classeDuPixel[p];
                if (retenue[j])
                {
                    double d = bande[p] - moy[j];
                    den[j] += d * d;
                }
            }
            double denum = 0;
            for (int j = 0; j < nbClasses; j++)
                if (retenue[j]) denum += den[j] / (nbrCouleur[j] - 1);

            // (e) Improved F-Score. Si toutes les classes sont parfaitement homogènes dans la bande
            //     (dénominateur nul), la bande sépare parfaitement les classes dès que leurs
            //     moyennes diffèrent (score infini), sinon elle n'apporte aucune information (0).
            if (denum > 0)
                return num / denum;
            return num > 0 ? double.PositiveInfinity : 0.0;
        }

        // =================================================================================
        // NORMALISATION DES SCORES : w_i = F_i / somme_k F_k  (0 <= w_i <= 1, somme des w_i = 1)
        // =================================================================================
        public static double[] NormaliserScores(List<double> scores)
        {
            int m = scores.Count;
            double[] w = new double[m];

            // Bandes de score infini (classes parfaitement séparées) : elles se partagent le poids total
            int nbInfinis = 0;
            for (int i = 0; i < m; i++)
                if (double.IsPositiveInfinity(scores[i])) nbInfinis++;
            if (nbInfinis > 0)
            {
                for (int i = 0; i < m; i++)
                    w[i] = double.IsPositiveInfinity(scores[i]) ? 1.0 / nbInfinis : 0.0;
                return w;
            }

            double somme = 0;
            for (int i = 0; i < m; i++)
                somme += scores[i];
            if (!(somme > 0))
                throw new InvalidOperationException("All the Improved F-Scores are equal to zero: no band separates the classes, so the scores cannot be normalized.");

            for (int i = 0; i < m; i++)
                w[i] = scores[i] / somme;
            return w;
        }

        // =================================================================================
        // RECHERCHE DES CLASSES DE L'IMAGE CLASSÉE
        // Chaque couleur (ou valeur) distincte est une classe ; on compte ses pixels n_j.
        // =================================================================================
        public static int[] IndexerClasses(double[] etiquettes, out List<double> couleur, out List<int> nbrCouleur)
        {
            couleur = new List<double>();
            nbrCouleur = new List<int>();
            Dictionary<double, int> indice = new Dictionary<double, int>();
            int[] classeDuPixel = new int[etiquettes.Length];
            for (int p = 0; p < etiquettes.Length; p++)
            {
                int j;
                if (!indice.TryGetValue(etiquettes[p], out j))
                {
                    j = couleur.Count;
                    indice.Add(etiquettes[p], j);
                    couleur.Add(etiquettes[p]);
                    nbrCouleur.Add(0);
                }
                classeDuPixel[p] = j;
                nbrCouleur[j]++;
            }
            return classeDuPixel;
        }

        // =================================================================================
        // LISTE DES BANDES DE L'IMAGE MULTIBANDE
        // =================================================================================
        public static List<SourceBande> PreparerBandes(List<string> fichiers)
        {
            List<SourceBande> sources = new List<SourceBande>();

            // Cas 1 : un seul fichier ENVI .hdr contenant toutes les bandes
            if (fichiers.Count == 1 && EstFichierEnvi(fichiers[0]))
            {
                EnteteEnvi env = LireEnteteEnvi(fichiers[0]);
                for (int b = 0; b < env.Bands; b++)
                {
                    string nom = env.BandNames.Count == env.Bands ? env.BandNames[b] : "Band " + (b + 1);
                    sources.Add(new SourceBande { Envi = env, Indice = b, Nom = nom });
                }
                return sources;
            }

            // Cas 2 : un fichier image par bande (un TIFF multipage fournit une bande par page)
            foreach (string f in fichiers)
            {
                if (EstFichierEnvi(f))
                    throw new InvalidOperationException("Select either a single .hdr file containing all the bands, or the set of band image files, but not both.");
                int nbTrames = NombreDeTrames(f);
                for (int t = 0; t < nbTrames; t++)
                {
                    string nom = nbTrames > 1 ? Path.GetFileName(f) + " (page " + (t + 1) + ")" : Path.GetFileName(f);
                    sources.Add(new SourceBande { Fichier = f, Indice = t, Nom = nom });
                }
            }
            return sources;
        }

        // Lecture des valeurs d'une bande (pixel p = y * largeur + x)
        public static double[] LireBande(SourceBande s, out int largeur, out int hauteur)
        {
            if (s.Envi != null)
            {
                largeur = s.Envi.Samples;
                hauteur = s.Envi.Lines;
                return LireBandeEnvi(s.Envi, s.Indice);
            }
            bool couleur;
            return LireImage(s.Fichier, s.Indice, false, out largeur, out hauteur, out couleur);
        }

        // Lecture de l'image classée (étiquette de chaque pixel)
        public static double[] LireEtiquettes(string fichier, out int largeur, out int hauteur, out bool couleur)
        {
            if (EstFichierEnvi(fichier))
            {
                // Image classée ENVI : on utilise sa première bande
                EnteteEnvi env = LireEnteteEnvi(fichier);
                largeur = env.Samples;
                hauteur = env.Lines;
                couleur = false;
                return LireBandeEnvi(env, 0);
            }
            return LireImage(fichier, 0, true, out largeur, out hauteur, out couleur);
        }

        // =================================================================================
        // LECTURE D'UN FICHIER IMAGE (TIFF, PNG, BMP, GIF, JPEG) AVEC WPF
        //   - niveaux de gris 8 bits, 16 bits ou flottant : valeurs radiométriques d'origine ;
        //   - image couleur : bande = canal rouge (R = V = B pour une image en niveaux de gris) ;
        //   - image classée couleur : étiquette = couleur complète (A, R, V, B) ;
        //   - image classée à palette (8 bits) : étiquette = indice de la palette.
        // =================================================================================
        public static double[] LireImage(string fichier, int trame, bool etiquette, out int largeur, out int hauteur, out bool couleur)
        {
            try
            {
                using (FileStream fs = new FileStream(fichier, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    BitmapDecoder dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                                                             BitmapCacheOption.Default);
                    BitmapSource src = dec.Frames[trame];
                    int W = src.PixelWidth, H = src.PixelHeight, n = W * H;
                    double[] v = new double[n];
                    PixelFormat f = src.Format;
                    couleur = false;

                    if (f == PixelFormats.Gray8)
                    {
                        byte[] px = new byte[n];
                        src.CopyPixels(px, W, 0);
                        for (int i = 0; i < n; i++) v[i] = px[i];
                    }
                    else if (f == PixelFormats.Gray16)
                    {
                        // TIFF 16 bits signés (SampleFormat = 2) : WPF les fournit comme non signés,
                        // on réinterprète donc chaque valeur en entier signé
                        bool signe = EstTiffSigne(src);
                        ushort[] px = new ushort[n];
                        src.CopyPixels(px, W * 2, 0);
                        for (int i = 0; i < n; i++) v[i] = signe ? (short)px[i] : px[i];
                    }
                    else if (f == PixelFormats.Gray32Float)
                    {
                        float[] px = new float[n];
                        src.CopyPixels(px, W * 4, 0);
                        for (int i = 0; i < n; i++) v[i] = px[i];
                    }
                    else if (f == PixelFormats.Indexed8 && etiquette)
                    {
                        byte[] px = new byte[n];
                        src.CopyPixels(px, W, 0);
                        for (int i = 0; i < n; i++) v[i] = px[i];
                    }
                    else if (f == PixelFormats.BlackWhite || f == PixelFormats.Gray2 || f == PixelFormats.Gray4)
                    {
                        FormatConvertedBitmap gris = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
                        byte[] px = new byte[n];
                        gris.CopyPixels(px, W, 0);
                        for (int i = 0; i < n; i++) v[i] = px[i];
                    }
                    else
                    {
                        // Tous les autres formats (couleur, palette...) sont convertis en BGRA 8 bits
                        FormatConvertedBitmap bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                        byte[] px = new byte[(long)n * 4];
                        bgra.CopyPixels(px, W * 4, 0);
                        for (int i = 0; i < n; i++)
                        {
                            if (etiquette)
                                v[i] = ((uint)px[4 * i + 3] << 24) | ((uint)px[4 * i + 2] << 16) | ((uint)px[4 * i + 1] << 8) | px[4 * i];
                            else
                                v[i] = px[4 * i + 2];   // canal rouge
                        }
                        couleur = etiquette;
                    }
                    largeur = W;
                    hauteur = H;
                    return v;
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                throw new InvalidDataException("Cannot read the image \"" + fichier + "\": " + ex.Message, ex);
            }
        }

        // Vrai si l'image est un TIFF à entiers signés (balise TIFF 339 SampleFormat = 2)
        private static bool EstTiffSigne(BitmapSource src)
        {
            try
            {
                BitmapMetadata md = src.Metadata as BitmapMetadata;
                if (md == null || !string.Equals(md.Format, "tiff", StringComparison.OrdinalIgnoreCase))
                    return false;
                object sf = md.GetQuery("/ifd/{ushort=339}");
                if (sf is Array a)
                    sf = a.Length > 0 ? a.GetValue(0) : null;
                return sf != null && Convert.ToInt32(sf, CultureInfo.InvariantCulture) == 2;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Nombre de trames (pages) d'un fichier image : 1 en général, plusieurs pour un TIFF multipage
        public static int NombreDeTrames(string fichier)
        {
            try
            {
                using (FileStream fs = new FileStream(fichier, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    BitmapDecoder dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                                                             BitmapCacheOption.Default);
                    return dec.Frames.Count;
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                throw new InvalidDataException("Cannot read the image \"" + fichier + "\": " + ex.Message, ex);
            }
        }

        // =================================================================================
        // LECTURE D'UNE IMAGE ENVI (.hdr + fichier de données binaire)
        // =================================================================================
        public static bool EstFichierEnvi(string fichier)
        {
            return string.Equals(Path.GetExtension(fichier), ".hdr", StringComparison.OrdinalIgnoreCase);
        }

        // Lecture de l'en-tête ENVI : dimensions, type de données, entrelacement, ordre des octets...
        public static EnteteEnvi LireEnteteEnvi(string fichierHdr)
        {
            string[] lignes = File.ReadAllLines(fichierHdr);
            if (lignes.Length == 0 || !lignes[0].Trim().StartsWith("ENVI", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("\"" + fichierHdr + "\" is not an ENVI header file (its first line must be \"ENVI\").");

            // Lecture des couples « clé = valeur » ; une valeur entre accolades peut tenir sur plusieurs lignes
            Dictionary<string, string> cles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lignes.Length; i++)
            {
                int egal = lignes[i].IndexOf('=');
                if (egal < 0) continue;
                string cle = lignes[i].Substring(0, egal).Trim();
                string valeur = lignes[i].Substring(egal + 1).Trim();
                if (valeur.StartsWith("{"))
                {
                    while (!valeur.Contains("}") && i + 1 < lignes.Length)
                    {
                        i++;
                        valeur += " " + lignes[i].Trim();
                    }
                    int debut = valeur.IndexOf('{');
                    int fin = valeur.LastIndexOf('}');
                    valeur = fin > debut ? valeur.Substring(debut + 1, fin - debut - 1).Trim() : valeur.Substring(debut + 1).Trim();
                }
                cles[cle] = valeur;
            }

            EnteteEnvi e = new EnteteEnvi();
            e.FichierHdr = fichierHdr;
            e.Samples = (int)EntierEnvi(cles, "samples", fichierHdr, -1);
            e.Lines = (int)EntierEnvi(cles, "lines", fichierHdr, -1);
            e.Bands = (int)EntierEnvi(cles, "bands", fichierHdr, -1);
            e.DataType = (int)EntierEnvi(cles, "data type", fichierHdr, -1);
            e.HeaderOffset = EntierEnvi(cles, "header offset", fichierHdr, 0);
            e.ByteOrder = (int)EntierEnvi(cles, "byte order", fichierHdr, 0);
            string s;
            e.Interleave = cles.TryGetValue("interleave", out s) ? s.Trim().ToLowerInvariant() : "bsq";

            if (e.Samples <= 0 || e.Lines <= 0 || e.Bands <= 0)
                throw new InvalidDataException("Invalid image size in \"" + fichierHdr + "\".");
            if (e.Interleave != "bsq" && e.Interleave != "bil" && e.Interleave != "bip")
                throw new InvalidDataException("Unknown interleave \"" + e.Interleave + "\" in \"" + fichierHdr + "\" (expected bsq, bil or bip).");
            if (cles.TryGetValue("file type", out s) && s.IndexOf("tiff", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new NotSupportedException("The header \"" + fichierHdr + "\" describes a TIFF file: select the TIFF band files instead of the .hdr file.");
            int taille = TailleTypeEnvi(e.DataType);

            if (cles.TryGetValue("band names", out s))
                foreach (string nom in s.Split(','))
                    if (nom.Trim().Length > 0) e.BandNames.Add(nom.Trim());

            // Fichier de données et contrôle de sa taille
            e.FichierDonnees = TrouverFichierDonneesEnvi(fichierHdr);
            long attendu = e.HeaderOffset + (long)e.Samples * e.Lines * e.Bands * taille;
            long reel = new FileInfo(e.FichierDonnees).Length;
            if (reel < attendu)
                throw new InvalidDataException("The data file \"" + e.FichierDonnees + "\" is too small (" + reel + " bytes) for the size described in its header ("
                                               + attendu + " bytes).");
            return e;
        }

        // Valeur entière d'une clé de l'en-tête ENVI (défaut < 0 : clé obligatoire)
        private static long EntierEnvi(Dictionary<string, string> cles, string cle, string fichierHdr, long defaut)
        {
            string s;
            long v;
            if (cles.TryGetValue(cle, out s) && long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return v;
            if (defaut >= 0 && !cles.ContainsKey(cle))
                return defaut;
            throw new InvalidDataException("Missing or invalid \"" + cle + "\" value in the ENVI header \"" + fichierHdr + "\".");
        }

        // Taille en octets d'une valeur selon le type de données ENVI
        private static int TailleTypeEnvi(int type)
        {
            switch (type)
            {
                case 1: return 1;    // octet non signé
                case 2: return 2;    // entier 16 bits signé
                case 3: return 4;    // entier 32 bits signé
                case 4: return 4;    // réel 32 bits
                case 5: return 8;    // réel 64 bits
                case 12: return 2;   // entier 16 bits non signé
                case 13: return 4;   // entier 32 bits non signé
                case 14: return 8;   // entier 64 bits signé
                case 15: return 8;   // entier 64 bits non signé
                default:
                    throw new NotSupportedException("ENVI data type " + type + " is not supported (complex data types are not handled).");
            }
        }

        // Fichier de données ENVI : même nom que le .hdr, sans extension ou avec .img, .dat, .raw...
        private static string TrouverFichierDonneesEnvi(string fichierHdr)
        {
            string complet = Path.GetFullPath(fichierHdr);
            string dossier = Path.GetDirectoryName(complet);
            string nomBase = Path.GetFileNameWithoutExtension(complet);
            string[] extensions = { "", ".img", ".dat", ".raw", ".bsq", ".bil", ".bip", ".bin", ".envi" };
            foreach (string ext in extensions)
            {
                string candidat = Path.Combine(dossier, nomBase + ext);
                if (File.Exists(candidat)) return candidat;
            }
            string[] exclues = { ".hdr", ".xml", ".aux", ".txt", ".enp", ".sta", ".ovr", ".tfw", ".prj", ".roi", ".evf" };
            foreach (string candidat in Directory.GetFiles(dossier, nomBase + ".*"))
            {
                if (Array.IndexOf(exclues, Path.GetExtension(candidat).ToLowerInvariant()) < 0)
                    return candidat;
            }
            throw new FileNotFoundException("The data file of \"" + fichierHdr + "\" was not found (expected: same name without extension, or with .img, .dat, .raw, .bsq, .bil or .bip).");
        }

        // Lecture de la bande b d'une image ENVI (pixel p = y * largeur + x)
        public static double[] LireBandeEnvi(EnteteEnvi e, int b)
        {
            int W = e.Samples, H = e.Lines, B = e.Bands, t = TailleTypeEnvi(e.DataType);
            bool grosBoutiste = e.ByteOrder == 1;
            double[] v = new double[(long)W * H];
            using (FileStream fs = new FileStream(e.FichierDonnees, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                if (e.Interleave == "bip")
                {
                    // BIP : pour chaque pixel, les valeurs de toutes les bandes se suivent
                    byte[] ligne = new byte[W * B * t];
                    for (int y = 0; y < H; y++)
                    {
                        fs.Seek(e.HeaderOffset + (long)y * W * B * t, SeekOrigin.Begin);
                        fs.ReadExactly(ligne, 0, ligne.Length);
                        for (int x = 0; x < W; x++)
                            v[(long)y * W + x] = ValeurEnvi(ligne, (x * B + b) * t, e.DataType, grosBoutiste);
                    }
                }
                else
                {
                    // BSQ : bande par bande ; BIL : pour chaque ligne, une ligne de chaque bande
                    byte[] ligne = new byte[W * t];
                    for (int y = 0; y < H; y++)
                    {
                        long position = e.Interleave == "bsq"
                            ? e.HeaderOffset + ((long)b * H + y) * W * t
                            : e.HeaderOffset + ((long)y * B + b) * W * t;
                        fs.Seek(position, SeekOrigin.Begin);
                        fs.ReadExactly(ligne, 0, ligne.Length);
                        for (int x = 0; x < W; x++)
                            v[(long)y * W + x] = ValeurEnvi(ligne, x * t, e.DataType, grosBoutiste);
                    }
                }
            }
            return v;
        }

        // Conversion d'une valeur binaire ENVI en nombre réel (byte order 0 = petit-boutiste, 1 = gros-boutiste)
        private static double ValeurEnvi(byte[] tampon, int position, int type, bool grosBoutiste)
        {
            ReadOnlySpan<byte> s = tampon.AsSpan(position);
            switch (type)
            {
                case 1: return tampon[position];
                case 2: return grosBoutiste ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s);
                case 3: return grosBoutiste ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s);
                case 4: return grosBoutiste ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s);
                case 5: return grosBoutiste ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s);
                case 12: return grosBoutiste ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
                case 13: return grosBoutiste ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
                case 14: return grosBoutiste ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s);
                case 15: return grosBoutiste ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s);
                default: throw new NotSupportedException("ENVI data type " + type + " is not supported.");
            }
        }

        // =================================================================================
        // SORTIE : FICHIER .TXT DES SCORES DANS LE DOSSIER « Scores of Bands »
        // =================================================================================

        // Dossier « Scores of Bands » : toujours juste sous le dossier du projet.
        // On remonte depuis le dossier de l'exécutable (bin\Debug\... ou Executable\) jusqu'au
        // dossier qui contient la solution (.sln) ; s'il n'y en a pas (exécutable copié seul sur
        // une autre machine), le dossier est créé à côté de l'exécutable.
        public static string DossierScores()
        {
            string dossierExe = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                DirectoryInfo d = new DirectoryInfo(dossierExe);
                while (d != null)
                {
                    if (d.GetFiles("*.sln").Length > 0)
                        return Path.Combine(d.FullName, "Scores of Bands");
                    d = d.Parent;
                }
            }
            catch (Exception)
            {
                // Dossier parent illisible : on garde le dossier de l'exécutable
            }
            return Path.Combine(dossierExe, "Scores of Bands");
        }

        // Lignes « Score of Band i noted IFS_i is : valeur » (affichage et fichier)
        public static string TexteScores(ResultatIFS r)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            double somme = 0;
            for (int i = 0; i < r.Scores.Length; i++)
            {
                sb.AppendLine("Score of Band " + (i + 1) + " noted IFS_" + (i + 1) + " is : " + r.Scores[i].ToString("F8", ci));
                somme += r.Scores[i];
            }
            sb.AppendLine("Sum of the scores : " + somme.ToString("F8", ci));
            return sb.ToString();
        }

        // Écriture du fichier des scores ; renvoie son chemin complet
        public static string EcrireFichierScores(string dossier, ResultatIFS r)
        {
            Directory.CreateDirectory(dossier);
            string chemin = Path.Combine(dossier, "IFS_Scores_" + r.NomBase + ".txt");
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Improved F‑Score Calculation Program : EEIC’2019 University of Bejaia");
            sb.AppendLine("Reference paper for the method: L’haddad, S., & Kemmouche, A. Hyperspectral Feature Selection using Improved F‑Score Technique (2019).");
            sb.AppendLine();
            sb.AppendLine("Date              : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ci));
            sb.AppendLine("Multiband image   : " + r.FichierMultibande);
            sb.AppendLine("Label image       : " + r.FichierEtiquettes);
            sb.AppendLine("Image size        : " + r.Largeur + " x " + r.Hauteur + " pixels");
            sb.AppendLine("Number of bands   : " + r.Scores.Length);
            sb.AppendLine("Number of classes : " + r.ClassesRetenues
                          + (r.ClassesIgnorees > 0 ? "  (+ " + r.ClassesIgnorees + " class(es) of a single pixel, ignored)" : ""));
            sb.AppendLine();
            sb.AppendLine("Normalized scores (weights): each score is between 0 and 1 and their sum is equal to 1.");
            sb.AppendLine();
            sb.Append(TexteScores(r));
            sb.AppendLine();

            // Poids prêts à coller dans les programmes de morphologie multivaluée (AHP, PROMETHEE...)
            string[] poids = new string[r.Scores.Length];
            for (int i = 0; i < r.Scores.Length; i++)
                poids[i] = r.Scores[i].ToString("F8", ci).Replace('.', ',');
            sb.AppendLine("Weights ready to paste into the multivalued morphology programs (separated by semicolons):");
            sb.AppendLine(string.Join(";", poids));
            sb.AppendLine();

            // Valeurs brutes F_i avant normalisation, avec le nom de chaque bande
            sb.AppendLine("Raw Improved F-Score values before normalization:");
            for (int i = 0; i < r.ScoresBruts.Count; i++)
            {
                string brut = double.IsPositiveInfinity(r.ScoresBruts[i])
                    ? "infinity (zero within-class variance)"
                    : r.ScoresBruts[i].ToString("G10", ci);
                sb.AppendLine("F_" + (i + 1) + " = " + brut + "   [" + r.NomsBandes[i] + "]");
            }
            sb.AppendLine();

            // Classes trouvées dans l'image classée
            sb.AppendLine("Classes of the label image (label : number of pixels):");
            for (int j = 0; j < r.ValeursClasses.Count; j++)
            {
                string etiquette = r.EtiquettesCouleur
                    ? "colour #" + ((uint)r.ValeursClasses[j]).ToString("X8", ci)
                    : "value " + r.ValeursClasses[j].ToString("G10", ci);
                sb.AppendLine("  " + etiquette + " : " + r.PixelsParClasse[j]
                              + (r.PixelsParClasse[j] < 2 ? "  (ignored: fewer than 2 pixels)" : ""));
            }

            File.WriteAllText(chemin, sb.ToString(), new UTF8Encoding(true));
            return chemin;
        }

        // =================================================================================
        // OUTILS
        // =================================================================================

        // Tri des noms de fichiers dans l'ordre de l'Explorateur Windows (B1, B2, ..., B10)
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);

        public static int ComparerNomsFichiers(string a, string b)
        {
            int c = StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b));
            return c != 0 ? c : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    // =====================================================================================
    // Description d'une bande : bande b d'un fichier ENVI, ou page t d'un fichier image
    // =====================================================================================
    public class SourceBande
    {
        public EnteteEnvi Envi;      // non nul : bande d'une image ENVI
        public string Fichier;       // sinon : fichier image
        public int Indice;           // numéro de bande (ENVI) ou de page (image)
        public string Nom;           // nom affiché dans le fichier des scores
    }

    // En-tête d'une image ENVI
    public class EnteteEnvi
    {
        public string FichierHdr, FichierDonnees;
        public int Samples, Lines, Bands, DataType, ByteOrder;
        public long HeaderOffset;
        public string Interleave = "bsq";
        public List<string> BandNames = new List<string>();
    }

    // Résultat du calcul
    public class ResultatIFS
    {
        public string FichierMultibande, FichierEtiquettes, NomBase, FichierScores;
        public int Largeur, Hauteur;
        public bool EtiquettesCouleur;                          // étiquettes = couleurs ARVB
        public List<double> ValeursClasses = new List<double>();
        public List<int> PixelsParClasse = new List<int>();
        public int ClassesRetenues, ClassesIgnorees, PixelsUtilises;
        public List<string> NomsBandes = new List<string>();
        public List<double> ScoresBruts = new List<double>();   // F_i
        public double[] Scores;                                  // F_i normalisés (somme = 1)
    }
}
