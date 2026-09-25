using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public static class AccessTpvFormatCatalog
{
    public static IReadOnlyList<AccessTpvFormatDoc> Document()
    {
        var date = new[]
        {
            "Left(nombre, 8) se usa como yyyymmdd (mover_ficheros_antiguos; companion ±1).",
            "CDate(Mid(nombre,7,2) & \"-\" & Mid(nombre,5,2) & \"-\" & Left(nombre,4)) = dd-MM-yyyy del prefijo."
        };
        var store = new[]
        {
            "TIENDA = Val(Left(Right(nombre, 16), 4)) — cuatro caracteres a partir de los 16 últimos.",
            "La caja no se lee del nombre; llega en el contenido o en QueryDefs sobre los txt reescritos."
        };
        var separators = new[]
        {
            "Al reescribir se antepone TIENDA y '|' (salvo l_tiq si la línea ya empieza por '|').",
            "Importar_Click recorta el literal '|2018|' en líneas de datos del 07 (Date>=30-05-2018, a>2, Len>50).",
            "ARQUEO omite líneas con InStr '|X|'."
        };
        var nameLayout = new[]
        {
            "Sustitución de tipo demostrada: Left(n, Len-11) & tipo2 & Right(n, 9). El código de tipo ocupa 2 caracteres en esa posición.",
            "No hay ficheros de ejemplo en ORIGENMDB; no se afirma el layout completo de columnas del interior."
        };
        return
        [
            new AccessTpvFormatDoc(
                "07",
                "d_red & \"20*_07_*.txt\"",
                "Importar_Click",
                "d_red",
                date,
                store,
                separators,
                ["h_red a0.txt", "h_red a1.txt", "h_red a2.txt", "h_red l_tiq.txt", "h_red tiq.txt", "h_red t_pag.txt"],
                ["05 → a1/tiq", "01 → a2/t_pag (fecha ±1 si falta y Len>25)"],
                nameLayout.Concat(
                [
                    "Fichero conductor de tiquets. Tras transformar, si TIENDA<>162 y <>33 y <200 llama tiquets_sin_pago, COBRADO, ventas_seccion, CREDITOS.",
                    "Registra el nombre 07 en fichero (AddNew)."
                ]).ToList(),
                ["Columnas internas del 07/05/01 desconocidas sin muestra.", "Significado de los 9 caracteres finales y del hueco entre TIENDA y el tipo: no demostrado."]),
            new AccessTpvFormatDoc(
                "05",
                "Companion: Left(n07, Len-11) & \"05\" & Right(n07, 9)",
                "Importar_Click (no tiene bucle propio)",
                "d_red",
                date,
                store,
                separators,
                ["h_red a1.txt", "h_red tiq.txt"],
                ["Del 07"],
                ["Se copia a a1.txt y se reescribe a tiq.txt con TIENDA|. TableDef TIQ → tiq.txt."],
                ["No se registra en fichero.", "Formato de línea pendiente de muestra."]),
            new AccessTpvFormatDoc(
                "01",
                "Companion: Left(n07, Len-11) & \"01\" & Right(n07, 9)",
                "Importar_Click (no tiene bucle propio)",
                "d_red",
                date,
                store,
                separators,
                ["h_red a2.txt", "h_red t_pag.txt"],
                ["Del 07"],
                ["Se copia a a2.txt y se reescribe a t_pag.txt con TIENDA|. TableDef t_pag → t_pag.txt."],
                ["No se registra en fichero.", "Formato de línea pendiente de muestra."]),
            new AccessTpvFormatDoc(
                "02",
                "d_red & \"20*_02_*.txt\"",
                "ARQUEO → DECLARADO",
                "d_red",
                date,
                store,
                separators,
                ["h_red a0.txt", "h_red a1.txt", "h_red a2.txt", "h_red a3.txt"],
                ["03 → a1/a3 (fecha ±1)"],
                nameLayout.Concat(
                [
                    "FileCopy 02→a0, reescribe a2.txt con TIENDA| omitiendo |X|.",
                    "TableDef a2 → A2.TXT (QueryDef arque).",
                    "Registra el nombre 02 en fichero tras DECLARADO."
                ]).ToList(),
                ["Columnas de arqueo pendientes de muestra.", "DECLARADO abre 'arque' sobre zetas.mdb, no CurrentDb."]),
            new AccessTpvFormatDoc(
                "03",
                "Companion: Left(n, Len-11) & \"03\" & Right(n, 9)",
                "ARQUEO y Importar_MONEDAS",
                "d_red",
                date,
                store,
                separators,
                ["h_red a1.txt", "h_red a3.txt"],
                ["Del 02 (arqueo) o del 04 (monedas)"],
                ["Reescribe a3.txt con TIENDA|. QueryDefs arque y moneditas usan A3 / a3."],
                ["No se registra en fichero.", "El mismo código 03 sirve a arqueo y a monedas; layout interior no verificado.", "mover_ficheros_antiguos hace Kill de 20*_03_*.txt antiguos."]),
            new AccessTpvFormatDoc(
                "06",
                "d_red & \"20*_06_*.txt\"",
                "PAGOS → pagos1",
                "d_red",
                date,
                store,
                ["Reescritura TIENDA| en a1.txt"],
                ["h_red a0.txt", "h_red a1.txt"],
                [],
                ["TableDef paguitos1 → a1.txt. QueryDef paguitos usa paguitos1 e i_divises.", "Registra el nombre 06 en fichero tras pagos1."],
                ["Columnas de pago pendientes de muestra.", "On Error GoTo 20 está declarado pero no activado en PAGOS."]),
            new AccessTpvFormatDoc(
                "00",
                "d_red & \"20*_00_*.txt\"",
                "cobros → cobros1",
                "d_red",
                date,
                store,
                ["Reescritura TIENDA| en a1.txt"],
                ["h_red a0.txt", "h_red a1.txt"],
                [],
                ["TableDef paguitos2 → a1.txt. QueryDef paguitos0. Importe se niega en cobros1.", "Registra el nombre 00 en fichero tras cobros1."],
                ["Mismo a1.txt que pagos: el proceso es secuencial (PAGOS luego cobros).", "Columnas pendientes de muestra."]),
            new AccessTpvFormatDoc(
                "04",
                "h_red & \"20*_04_*.txt\" tras MONEDAS (INBOX *_04_*.*)",
                "MONEDAS → Importar_MONEDAS → GRABAR_MONEDAS",
                "dd_red\\INBOX luego h_red",
                date,
                store,
                ["Reescritura TIENDA| en MONEDAS.txt y a3.txt"],
                ["h_red a0.txt", "h_red a1.txt", "h_red MONEDAS.txt", "h_red a3.txt"],
                ["03 desde d_red"],
                [
                    "MONEDAS: Dir dd_red\\INBOX\\*_04_*.*; IPZ → Shell C:\\tpvision\\cdados.EXE D ... TXT; si no FileCopy a h_red; Kill origen.",
                    "Importar_MONEDAS: 20*_04_*.txt en h_red, companion 03, TableDef moneditas0 → monedas.txt.",
                    "Registra el nombre 04 en fichero tras GRABAR_MONEDAS. Tope 1000 ficheros."
                ],
                ["Layout IPZ y posición Val(Mid(FIC,15,...)) no comprobados sin muestra.", "cdados.EXE no se ejecutó.", "id_divisa=10 se omite en GRABAR_MONEDAS (tarjeta propia va por otro camino)."])
        ];
    }
}
