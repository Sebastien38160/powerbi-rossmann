// =====================================================================
//  GENERATEUR DE MESURES DE BASE  —  script Tabular Editor
//  A executer sur N IMPORTE QUEL modele semantique ouvert dans Power BI.
//
//  Il analyse le modele et cree :
//    - SUM            sur chaque colonne numerique des tables de faits
//    - COUNTROWS      sur chaque table de faits
//    - DISTINCTCOUNT  sur chaque colonne identifiante
//    - un GROUPE DE CALCUL "Temps" (MTD, QTD, YTD, N-1, variations, 12 mois)
//
//  Il ne touche jamais a une mesure qui existe deja.
//
//  UTILISATION
//    Power BI Desktop > Outils externes > Tabular Editor
//    > onglet Advanced Scripting > coller > F5 > Ctrl+S
// =====================================================================

// ---------------------- REGLAGES ----------------------
var TABLE_MESURES   = "_Mesures";      // table qui accueille les mesures
var NOM_GROUPE      = "Temps";         // nom du groupe de calcul (vide = ne pas creer)
var DOSSIER_VOLUMES = "00 - Volumes";
var DOSSIER_INDIC   = "01 - Indicateurs";
var FORMAT_ENTIER   = "#,0";
var FORMAT_DECIMAL  = "#,0.00";
var GENERER_SUM     = true;
var GENERER_COUNT   = true;
var GENERER_DISTINCT= true;
var GENERER_MOYENNE = true;    // AVERAGE par colonne numerique
var GENERER_MINMAX  = false;   // MIN et MAX par colonne numerique (bruyant)
var GENERER_RATIOS  = false;   // DIVIDE par mesure (le groupe "Analyse" fait deja mieux)
var GENERER_RANGS   = false;   // RANKX par dimension (peut generer beaucoup de mesures)
var DOSSIER_RATIOS  = "02 - Ratios";
var DOSSIER_RANGS   = "03 - Classements";
var FORMAT_POURCENT = "0.0 %;-0.0 %;0.0 %";
var NOM_GROUPE_ANA  = "Analyse";  // 2e groupe de calcul (vide = ne pas creer)
var GENERER_STATS   = false;      // MEDIAN, ECARTYPE, PERCENTILE 90
var GENERER_QUALITE = false;      // taux de completude par colonne (COUNTBLANK)
// ------------------------------------------------------

var MOTS_ID   = new[]{"id","code","cle","key","identifiant","num","no","reference","ref","sk","pk","matricule","ean","siret"};
// colonnes deja exprimees en ratio : on ne les additionne JAMAIS
var MOTS_RATIO= new[]{"pct","pourcentage","pourcent","taux","ratio","rate","part","indice","moyenne","moyen","avg","median","densite","prix unitaire"};
// colonnes techniques : ni somme, ni moyenne, ni comptage distinct
var MOTS_TECH = new[]{"index","unnamed","ligne","row","rownum","ordre","tri","sort"};
var MOTS_DATE = new[]{"annee","year","mois","month","trimestre","quarter","semaine","week","jour","day","date"};

Func<string,string> norm = (s) => {
    s = System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])", " ");
    s = s.ToLowerInvariant();
    var sb = new System.Text.StringBuilder();
    foreach(var ch in s.Normalize(System.Text.NormalizationForm.FormD))
        if(System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            sb.Append(ch);
    s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "[^a-z0-9]+", " ").Trim();
    return " " + s + " ";
};
Func<string,string[],bool> aMot = (nom, mots) => {
    var n = norm(nom);
    foreach(var m in mots) if(n.Contains(" " + m + " ")) return true;
    return false;
};
Func<Column,bool> estNum = (c) =>
    c.DataType == DataType.Int64 || c.DataType == DataType.Double || c.DataType == DataType.Decimal;

// ---------- 0. NETTOYAGE des mesures cassees ----------
// Supprime les mesures qui referencent une table inexistante dans CE modele
// (typiquement : un script genere pour un autre modele).
{
    var nomsTables = new HashSet<string>(Model.Tables.Select(x => x.Name));
    var rx = new System.Text.RegularExpressions.Regex(@"'([^']+)'\s*\[");
    int supprimees = 0;
    foreach(var m in Model.AllMeasures.ToList())
    {
        if(string.IsNullOrEmpty(m.Expression)) continue;
        bool casse = false;
        foreach(System.Text.RegularExpressions.Match mt in rx.Matches(m.Expression))
            if(!nomsTables.Contains(mt.Groups[1].Value)) { casse = true; break; }
        if(casse) { m.Delete(); supprimees++; }
    }
    if(supprimees > 0) Output(supprimees + " mesure(s) cassee(s) supprimee(s).");
}

// ---------- 1. reperage des tables ----------
var vraiesTables = Model.Tables
    .Where(t => !(t is CalculationGroupTable))
    .Where(t => !t.Name.StartsWith("LocalDateTable_") && !t.Name.StartsWith("DateTableTemplate_"))
    .Where(t => t.Name != TABLE_MESURES)
    .ToList();

var cotePlusieurs = new HashSet<string>(
    Model.Relationships.OfType<SingleColumnRelationship>().Select(r => r.FromTable.Name));

var faits = vraiesTables.Where(t => cotePlusieurs.Contains(t.Name)
                                 && t.Columns.Any(c => estNum(c))).ToList();

Table tDate = null; Column cDate = null;
foreach(var t in vraiesTables)
{
    var dt = t.Columns.FirstOrDefault(c => c.DataType == DataType.DateTime);
    if(dt == null) continue;
    var parts = t.Columns.Count(c => aMot(c.Name, MOTS_DATE));
    if(parts >= 3 && parts >= t.Columns.Count * 0.4 && !faits.Contains(t)) { tDate = t; cDate = dt; break; }
}
if(tDate == null)
    foreach(var t in vraiesTables)
    {
        if(t.DataCategory != "Time") continue;
        var dt = t.Columns.FirstOrDefault(c => c.DataType == DataType.DateTime);
        if(dt != null) { tDate = t; cDate = dt; break; }
    }

if(faits.Count == 0) { Error("Aucune table de faits detectee. Verifie que ton modele a des relations."); return; }

// ---------- 2. table d accueil ----------
Table cible = Model.Tables.Contains(TABLE_MESURES)
    ? Model.Tables[TABLE_MESURES]
    : Model.AddCalculatedTable(TABLE_MESURES, "ROW(\"x\", BLANK())");
foreach(var c in cible.Columns) c.IsHidden = true;

// ---------- 3. generation des mesures ----------
int cree = 0, saute = 0;
var journal = new List<string>();

Action<string,string,string,string> ajouter = (nom, dax, fmt, dossier) => {
    if(cible.Measures.Contains(nom) || Model.AllMeasures.Any(x => x.Name == nom)) { saute++; return; }
    var m = cible.AddMeasure(nom, dax, dossier);
    m.FormatString = fmt;
    cree++;
    journal.Add("  + " + nom);
};

var nomTotal   = new Dictionary<string,string>();   // "table|colonne" -> nom mesure SUM
var nomLignes  = new Dictionary<string,string>();   // table            -> nom mesure COUNTROWS
var nomDistinct= new Dictionary<string,string>();   // "table|colonne" -> nom mesure DISTINCTCOUNT

foreach(var t in faits)
{
    if(GENERER_COUNT)
    {
        var nl = "Nb lignes " + t.Name;
        ajouter(nl, "COUNTROWS ( '" + t.Name + "' )", FORMAT_ENTIER, DOSSIER_VOLUMES);
        nomLignes[t.Name] = nl;
    }

    foreach(var c in t.DataColumns)
    {
        if(!estNum(c)) continue;
        if(aMot(c.Name, MOTS_DATE)) continue;
        if(aMot(c.Name, MOTS_ID)) continue;
        if(aMot(c.Name, MOTS_TECH)) continue;          // colonne technique : on ignore

        // une colonne deja en % / taux / indice ne doit jamais etre additionnee
        bool estRatio = c.Name.Contains("%") || aMot(c.Name, MOTS_RATIO);

        var fmt = c.DataType == DataType.Int64 ? FORMAT_ENTIER : FORMAT_DECIMAL;
        var reference = "'" + t.Name + "'[" + c.Name + "]";

        if(GENERER_SUM && !estRatio)
        {
            var nt = "Total " + c.Name;
            ajouter(nt, "SUM ( " + reference + " )", fmt, DOSSIER_INDIC);
            nomTotal[t.Name + "|" + c.Name] = nt;
        }
        if(GENERER_MOYENNE)
            ajouter("Moyenne " + c.Name, "AVERAGE ( " + reference + " )",
                    estRatio ? FORMAT_POURCENT : FORMAT_DECIMAL,
                    estRatio ? "08 - Moyennes de taux (non ponderees)" : DOSSIER_INDIC);
        if(GENERER_MINMAX)
        {
            ajouter("Min " + c.Name, "MIN ( " + reference + " )", fmt, DOSSIER_INDIC);
            ajouter("Max " + c.Name, "MAX ( " + reference + " )", fmt, DOSSIER_INDIC);
        }
        if(GENERER_STATS)
        {
            ajouter("Mediane " + c.Name, "MEDIAN ( " + reference + " )", FORMAT_DECIMAL, DOSSIER_INDIC);
            ajouter("Ecart-type " + c.Name, "STDEV.P ( " + reference + " )", FORMAT_DECIMAL, DOSSIER_INDIC);
            ajouter("P90 " + c.Name,
                    "PERCENTILEX.INC ( '" + t.Name + "', " + reference + ", 0.9 )", FORMAT_DECIMAL, DOSSIER_INDIC);
        }
        if(GENERER_QUALITE)
            ajouter("Completude " + c.Name + " %",
                    "DIVIDE ( COUNTROWS ( '" + t.Name + "' ) - COUNTBLANK ( " + reference + " ), COUNTROWS ( '" + t.Name + "' ) )",
                    FORMAT_POURCENT, "09 - Qualite des donnees");
    }
}

if(GENERER_DISTINCT)
    foreach(var t in vraiesTables)
        foreach(var c in t.DataColumns)
        {
            if(!aMot(c.Name, MOTS_ID)) continue;
            if(aMot(c.Name, MOTS_TECH)) continue;
            if(c.DataType == DataType.DateTime) continue;
            var nd = "Nb " + c.Name + " distincts";
            ajouter(nd, "DISTINCTCOUNT ( '" + t.Name + "'[" + c.Name + "] )",
                    FORMAT_ENTIER, DOSSIER_VOLUMES);
            nomDistinct[t.Name + "|" + c.Name] = nd;
        }

// ---------- 3bis. RATIOS (DIVIDE) et CLASSEMENTS (RANKX) ----------
// Ces mesures ne referencent QUE d autres mesures : elles sont portables
// telles quelles vers un autre modele.
Func<string,bool> existe = (nom) => Model.AllMeasures.Any(x => x.Name == nom);

if(GENERER_RATIOS)
{
    foreach(var t in faits)
    {
        if(!nomLignes.ContainsKey(t.Name)) continue;
        var nl = nomLignes[t.Name];

        foreach(var kv in nomTotal)
        {
            if(!kv.Key.StartsWith(t.Name + "|")) continue;
            var col = kv.Key.Substring(t.Name.Length + 1);
            var nt  = kv.Value;
            if(!existe(nt) || !existe(nl)) continue;

            // moyenne par ligne de fait
            ajouter(col + " moyen par ligne",
                    "DIVIDE ( [" + nt + "], [" + nl + "] )",
                    FORMAT_DECIMAL, DOSSIER_RATIOS);

            // moyenne par entite (une par colonne identifiante de la table de faits)
            foreach(var kd in nomDistinct)
            {
                if(!kd.Key.StartsWith(t.Name + "|")) continue;
                var cle = kd.Key.Substring(t.Name.Length + 1);
                if(!existe(kd.Value)) continue;
                ajouter(col + " moyen par " + cle,
                        "DIVIDE ( [" + nt + "], [" + kd.Value + "] )",
                        FORMAT_DECIMAL, DOSSIER_RATIOS);
            }

            // part du total general
            ajouter("Part " + col + " %",
                    "DIVIDE ( [" + nt + "], CALCULATE ( [" + nt + "], REMOVEFILTERS () ) )",
                    FORMAT_POURCENT, DOSSIER_RATIOS);
        }
    }
}

if(GENERER_RANGS)
{
    var dims = vraiesTables.Where(t => !faits.Contains(t) && t != tDate).ToList();
    foreach(var d in dims)
    {
        var cle = d.DataColumns.FirstOrDefault(c => aMot(c.Name, MOTS_ID));
        if(cle == null) continue;
        foreach(var kv in nomTotal)
        {
            if(!existe(kv.Value)) continue;
            var col = kv.Key.Substring(kv.Key.IndexOf('|') + 1);
            ajouter("Rang " + d.Name + " par " + col,
                    "IF ( NOT ISBLANK ( [" + kv.Value + "] ),\r\n"
                  + "    RANKX ( ALL ( '" + d.Name + "'[" + cle.Name + "] ), [" + kv.Value + "],, DESC, DENSE ) )",
                    FORMAT_ENTIER, DOSSIER_RANGS);
        }
    }
}

// ---------- 4. groupe de calcul temporel ----------
int items = 0;
if(NOM_GROUPE != "" && tDate != null && cDate != null)
{
    var d = "'" + tDate.Name + "'[" + cDate.Name + "]";
    var defs = new List<string[]>{
      new[]{"Actuel",               "SELECTEDMEASURE ()"},
      new[]{"Cumul mois (MTD)",     "TOTALMTD ( SELECTEDMEASURE (), " + d + " )"},
      new[]{"Cumul trimestre (QTD)","TOTALQTD ( SELECTEDMEASURE (), " + d + " )"},
      new[]{"Cumul annee (YTD)",    "TOTALYTD ( SELECTEDMEASURE (), " + d + " )"},
      new[]{"Annee precedente",     "CALCULATE ( SELECTEDMEASURE (), SAMEPERIODLASTYEAR ( " + d + " ) )"},
      new[]{"YTD annee precedente", "CALCULATE ( TOTALYTD ( SELECTEDMEASURE (), " + d + " ), SAMEPERIODLASTYEAR ( " + d + " ) )"},
      new[]{"Variation N-1",
            "VAR _a = SELECTEDMEASURE ()\r\n" +
            "VAR _p = CALCULATE ( SELECTEDMEASURE (), SAMEPERIODLASTYEAR ( " + d + " ) )\r\n" +
            "RETURN IF ( NOT ISBLANK ( _a ) && NOT ISBLANK ( _p ), _a - _p )"},
      new[]{"Variation N-1 %",
            "VAR _a = SELECTEDMEASURE ()\r\n" +
            "VAR _p = CALCULATE ( SELECTEDMEASURE (), SAMEPERIODLASTYEAR ( " + d + " ) )\r\n" +
            "RETURN DIVIDE ( _a - _p, _p )"},
      new[]{"12 mois glissants",
            "CALCULATE ( SELECTEDMEASURE (), DATESINPERIOD ( " + d + ", MAX ( " + d + " ), -12, MONTH ) )"},
      new[]{"Mois precedent",
            "CALCULATE ( SELECTEDMEASURE (), DATEADD ( " + d + ", -1, MONTH ) )"},
      new[]{"Variation mois precedent %",
            "VAR _a = SELECTEDMEASURE ()\r\n" +
            "VAR _p = CALCULATE ( SELECTEDMEASURE (), DATEADD ( " + d + ", -1, MONTH ) )\r\n" +
            "RETURN DIVIDE ( _a - _p, _p )"},
      new[]{"Moyenne mensuelle sur 3 mois",
            "DIVIDE ( CALCULATE ( SELECTEDMEASURE (), DATESINPERIOD ( " + d + ", MAX ( " + d + " ), -3, MONTH ) ), 3 )"},
      new[]{"Cumul depuis l origine",
            "CALCULATE ( SELECTEDMEASURE (), DATESBETWEEN ( " + d + ", BLANK (), MAX ( " + d + " ) ) )"},
      new[]{"Part du total affiche %",
            "DIVIDE ( SELECTEDMEASURE (), CALCULATE ( SELECTEDMEASURE (), REMOVEFILTERS () ) )"}
    };

    CalculationGroupTable cg;
    if(Model.Tables.Contains(NOM_GROUPE) && Model.Tables[NOM_GROUPE] is CalculationGroupTable)
        cg = (CalculationGroupTable)Model.Tables[NOM_GROUPE];
    else if(Model.Tables.Contains(NOM_GROUPE))
        { Error("Une table nommee '" + NOM_GROUPE + "' existe deja et n est pas un groupe de calcul. Renomme NOM_GROUPE en haut du script."); return; }
    else
        cg = Model.AddCalculationGroup(NOM_GROUPE);

    int ord = 0;
    foreach(var def in defs)
    {
        if(cg.CalculationItems.Contains(def[0])) { ord++; continue; }
        var ci = cg.AddCalculationItem(def[0], def[1]);
        ci.Ordinal = ord++;
        items++;
    }
    foreach(var ci in cg.CalculationItems)
        if(ci.Name.EndsWith("%")) ci.FormatStringExpression = "\"0.0 %;-0.0 %;0.0 %\"";
}

// ---------- 4bis. GROUPE DE CALCUL "Analyse" ----------
// Motifs transversaux : au lieu de generer "X moyen par ligne" pour chaque X,
// un seul element s applique a TOUTES les mesures du modele.
int itemsAna = 0;
if(NOM_GROUPE_ANA != "")
{
    var faitPrincipal = faits.OrderByDescending(t => t.DataColumns.Count()).First();
    var mLignes = nomLignes.ContainsKey(faitPrincipal.Name) ? nomLignes[faitPrincipal.Name] : null;
    var cleAna  = nomDistinct.FirstOrDefault(k => k.Key.StartsWith(faitPrincipal.Name + "|"));

    var defsA = new List<string[]>();
    defsA.Add(new[]{"Valeur", "SELECTEDMEASURE ()"});
    defsA.Add(new[]{"Part du total general %",
        "DIVIDE ( SELECTEDMEASURE (), CALCULATE ( SELECTEDMEASURE (), REMOVEFILTERS () ) )"});
    defsA.Add(new[]{"Part du total affiche %",
        "DIVIDE ( SELECTEDMEASURE (), CALCULATE ( SELECTEDMEASURE (), ALLSELECTED () ) )"});
    if(mLignes != null && Model.AllMeasures.Any(x => x.Name == mLignes))
        defsA.Add(new[]{"Moyenne par ligne",
            "DIVIDE ( SELECTEDMEASURE (), [" + mLignes + "] )"});
    if(cleAna.Value != null && Model.AllMeasures.Any(x => x.Name == cleAna.Value))
        defsA.Add(new[]{"Moyenne par " + cleAna.Key.Substring(faitPrincipal.Name.Length + 1),
            "DIVIDE ( SELECTEDMEASURE (), [" + cleAna.Value + "] )"});
    defsA.Add(new[]{"Ecart a la moyenne affichee",
        "VAR _v = SELECTEDMEASURE ()\r\n" +
        "VAR _m = AVERAGEX ( ALLSELECTED (), SELECTEDMEASURE () )\r\n" +
        "RETURN _v - _m"});
    defsA.Add(new[]{"Indice base 100 vs total",
        "DIVIDE ( SELECTEDMEASURE (), CALCULATE ( SELECTEDMEASURE (), REMOVEFILTERS () ) ) * 100"});

    CalculationGroupTable cga = null;
    if(Model.Tables.Contains(NOM_GROUPE_ANA) && Model.Tables[NOM_GROUPE_ANA] is CalculationGroupTable)
        cga = (CalculationGroupTable)Model.Tables[NOM_GROUPE_ANA];
    else if(!Model.Tables.Contains(NOM_GROUPE_ANA))
        cga = Model.AddCalculationGroup(NOM_GROUPE_ANA);

    if(cga != null)
    {
        int oa = 0;
        foreach(var def in defsA)
        {
            if(cga.CalculationItems.Contains(def[0])) { oa++; continue; }
            var ci = cga.AddCalculationItem(def[0], def[1]);
            ci.Ordinal = oa++;
            itemsAna++;
        }
        foreach(var ci in cga.CalculationItems)
            if(ci.Name.EndsWith("%")) ci.FormatStringExpression = "\"0.0 %;-0.0 %;0.0 %\"";
        // priorite : Temps s applique avant Analyse
        cga.CalculationGroup.Precedence = 10;
        if(Model.Tables.Contains(NOM_GROUPE) && Model.Tables[NOM_GROUPE] is CalculationGroupTable)
            ((CalculationGroupTable)Model.Tables[NOM_GROUPE]).CalculationGroup.Precedence = 20;
    }
}

// ---------- 5. compte rendu ----------
var msg = "TABLES DE FAITS DETECTEES : " + string.Join(", ", faits.Select(t => t.Name)) + "\r\n"
        + "TABLE DE DATES : " + (tDate == null ? "AUCUNE (groupe de calcul non cree)" : tDate.Name + "[" + cDate.Name + "]") + "\r\n\r\n"
        + cree + " mesure(s) creee(s), " + saute + " deja existante(s) et laissee(s) intacte(s).\r\n"
        + items + " element(s) temporel(s) + " + itemsAna + " element(s) d analyse cree(s).\r\n\r\n"
        + string.Join("\r\n", journal.Take(40))
        + (journal.Count > 40 ? "\r\n  ... et " + (journal.Count - 40) + " autres" : "")
        + "\r\n\r\nPense a faire Ctrl+S pour renvoyer dans Power BI Desktop.";
Info(msg);
