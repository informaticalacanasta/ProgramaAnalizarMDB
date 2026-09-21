namespace MdbToSql.Core.Analysis;

internal sealed record AccessProcedureOverlay(
    string Purpose,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> TablesRead,
    IReadOnlyList<string> TablesWritten,
    IReadOnlyList<string> Queries,
    IReadOnlyList<string> Transformations,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> ErrorHandling);

internal static class AccessImportarProcedureCatalog
{
    public const string CajaRemap =
        "Remapeo de CAJA: tienda 92 y CAJA 2|3|5 → 2; tienda 91 y CAJA 2|3|4 → 2; tiendas 65|66 y CAJA 3|4 → 3; tienda 62 y CAJA 4|5 → 4; tienda 68 y CAJA 4|2|6 → 2; resto CAJA original. (tienda=92 está duplicado en el If).";

    public const string TiendaTicketFilter =
        "TIENDA <> 162 And TIENDA <> 33 And TIENDA < 200";

    public const string ZetasPath =
        "Si LCase(OPERADOR)=\"supervisores\" abre c:\\programas\\transmisiones\\zetas.mdb; si no, \\\\supervisores\\c\\programas\\transmisiones\\zetas.mdb.";

    public static AccessProcedureOverlay? For(string name)
    {
        return name.ToUpperInvariant() switch
        {
            "IMPORTAR_CLICK" => ImportarClick(),
            "TIQUETS_SIN_PAGO" => TiquetsSinPago(),
            "COBRADO" => Cobrado(),
            "VENTAS_SECCION" => VentasSeccion(),
            "CREDITOS" => Creditos(),
            "ARQUEO" => Arqueo(),
            "DECLARADO" => Declarado(),
            "PAGOS" => Pagos(),
            "PAGOS1" => Pagos1(),
            "COBROS" => Cobros(),
            "COBROS1" => Cobros1(),
            "MONEDAS" => Monedas(),
            "IMPORTAR_MONEDAS" => ImportarMonedas(),
            "GRABAR_MONEDAS" => GrabarMonedas(),
            "MOVER_FICHEROS_ANTIGUOS" => MoverFicheros(),
            "PRV_FINALES_CLICK" => PrvFinales(),
            "CREA_FIC" => CreaFic(),
            "FILTROS_1" => Filtros1(),
            "COMPROBAR_PRV_FINAL" => ComprobarPrvFinal(),
            "GRABAR_TARJETA_PROPIA" => GrabarTarjeta(),
            "FORM_OPEN" => FormOpen(),
            _ => null
        };
    }

    private static AccessProcedureOverlay ImportarClick() => new(
        "Ingesta de ficheros de tiquets 20*_07_*.txt aún no registrados en fichero: copia/transforma companions, antepone TIENDA, importa ventas/créditos si pasa el filtro de tienda y después marca el nombre como procesado. Al terminar el bucle dispara arqueo, pagos, cobros, monedas, archivo y prv_finales.",
        [
            "OPERADOR (elige ruta de zetas.mdb)",
            "d_red (Form_Open: \\\\pedidos\\c\\tpvision\\received\\received2\\)",
            "h_red (Form_Open: \\\\pedidos\\c\\ipv\\received\\)",
            "sql_buff100() lista de nombres 20*_07_*.txt no presentes en fichero",
            "TIENDA = Val(Left(Right(nombre, 16), 4))"
        ],
        [
            "LCase(OPERADOR)=\"supervisores\" decide ruta local vs UNC de zetas.mdb",
            "tabla.Seek nombre: si existe en fichero, GoTo 30 (no se encola)",
            "Date >= 30-05-2018: recorta |2018| en líneas >2 de longitud >50; si no, FileCopy a a0.txt",
            "Companion tipo 05 y 01: si falta, prueba fecha +1 y -1 cuando Len(nombre)>25; si Len<=25 GoTo 40",
            TiendaTicketFilter + " para llamar tiquets_sin_pago/COBRADO/ventas_seccion/CREDITOS"
        ],
        ["fichero (Seek índice fichero)"],
        ["fichero (AddNew fichero=nombre tras procesar el tiquet 07)"],
        [],
        [
            "Patrón de nombre: Left(n, Len-11) & tipo & Right(n, 9) sustituye el código de tipo (07→05 líneas tiquet, 07→01 pagos)",
            "Desde 30-05-2018: Mid desde |2018| en líneas de datos (no las dos primeras)",
            "Prefijo TIENDA: l_tiq.txt usa TIENDA + línea o TIENDA|línea si no empieza por |; tiq.txt y t_pag.txt siempre TIENDA|línea"
        ],
        [
            "Lee d_red & 20*_07_*.txt",
            "Escribe h_red a0.txt, a1.txt, a2.txt, l_tiq.txt, tiq.txt, t_pag.txt",
            "Copia companions d_red 05 y 01 (o fecha ±1) a a1.txt / a2.txt",
            "Abre zetas.mdb (local o UNC según OPERADOR)"
        ],
        [
            "On Error GoTo 20 alrededor de Dir/FileCopy de companions 05/01; 20: Resume 40 salta AddNew",
            "On Error GoTo 0 antes del filtro TIENDA y tras el bucle",
            "Etiqueta 40: Next (siguiente fichero sin registrar el actual)"
        ]);

    private static AccessProcedureOverlay TiquetsSinPago() => new(
        "Inserta en venta_clientes2 y consumos1 las líneas de tiquets sin forma de pago, separando clientes reales de consumos (id_client 0 o 10000).",
        ["CurrentDb", "QueryDefs tiquets_sin_pago y tiquets_sin_pago1 (vía INSERT SELECT)"],
        [
            "id_client > 0 And id_client <> 10000 → venta_clientes2",
            "id_client = 0 Or id_client = 10000 → consumos1"
        ],
        ["tiquets_sin_pago", "tiquets_sin_pago1"],
        ["venta_clientes2", "consumos1"],
        ["tiquets_sin_pago", "tiquets_sin_pago1"],
        [
            "INSERT INTO venta_clientes2 SELECT * FROM tiquets_sin_pago WHERE id_client > 0 AND id_client <> 10000",
            "INSERT INTO consumos1 SELECT * FROM tiquets_sin_pago1 WHERE id_client = 0 OR id_client = 10000"
        ],
        [],
        ["On Error Resume Next en ambos Execute: un fallo no aborta ni se registra"]);

    private static AccessProcedureOverlay Cobrado() => new(
        "Acumula el cobrado de la QueryDef cobrado sobre zetas.mdb (VENTA+=COBRADO, ARQUEO = max). Omite cajas bloqueadas.",
        ["OPERADOR", "CurrentDb tiquets_pagg (solo para RecordCount)", "QueryDef cobrado", "zetas índice zeta"],
        [
            ZetasPath,
            "Si tiquets_pagg.RecordCount = 0 no abre cobrado",
            TiendaTicketFilter + " sobre cada fila de cobrado",
            "COBRADO no nulo y <> 0",
            "Si zetas.bloqueado Then log y GoTo 30 (no escribe esa fila)",
            CajaRemap
        ],
        ["tiquets_pagg", "cobrado", "zetas"],
        ["zetas (Edit VENTA/ARQUEO o AddNew TIPO=Z)"],
        ["tiquets_pagg", "cobrado"],
        [
            "VENTA = VENTA + COBRADO",
            "ARQUEO: si zetas.ARQUEO es Null/0 toma cobrado.ARQUEO; si no, max(existente, cobrado.ARQUEO)",
            "Alta: TIENDA, FECHA, TIPO=\"Z\", CAJA remapeada, ARQUEO, VENTA=COBRADO"
        ],
        [
            "C:\\programas\\transmisiones\\log_yymmdd.txt y logS_yymm.txt si caja bloqueada",
            "zetas.mdb local o UNC"
        ],
        ["On Error GoTo 20: End Function (corta el resto de filas)", "GoTo 30 salta Update de la fila bloqueada"]);

    private static AccessProcedureOverlay VentasSeccion() => new(
        "Reparte ventas por sección en v_seccion, escribe claves en prv_final_MES_ANO.LINEAS y llama GRABAR_TARJETA_PROPIA si TARJETA<>0. Si no hay lineas_tiquets, toma fecha/tienda/caja de cobrado para el ajuste de sección 4.",
        [
            "OPERADOR",
            "lineas_tiquets",
            "v_seccion índice zeta",
            "zetas índice zeta",
            "ULTIMO_MES/ANO para prv_final_{mes}_{ano}.mdb"
        ],
        [
            ZetasPath,
            "Salta fila si id_arqueig Null o 0, o Year(FECHA)<2020 (primer bucle) / <2019 (segundo bucle LINEAS)",
            "comprueba_tienda está comentado; comprobadas se pone True en cada fila válida",
            "Si zetas.bloqueado en el Seek de la sección, GoTo 30 (no acumula esa línea en v_seccion)",
            "Ajuste sección 4 solo si TIENDA>0 And Not comprobadas, y en el tramo final también " + TiendaTicketFilter,
            CajaRemap
        ],
        ["lineas_tiquets", "zetas", "v_seccion", "tiquets_pagg (fallback)", "cobrado (fallback)", "LINEAS en prv_final_*.mdb"],
        ["v_seccion", "LINEAS (prv_final)", "monedas vía GRABAR_TARJETA_PROPIA"],
        ["lineas_tiquets", "tiquets_pagg", "cobrado"],
        [
            "VENTA de línea se suma a v_seccion por (TIENDA, FECHA, caja2, seccion)",
            "Cambio de FECHA: diferencia zetas.VENTA - SUM(v_seccion) se echa a seccion=4 (SQL dinámico con cajas remapeadas)",
            "Bug demostrado L218 primer tramo: venta_seccion = tabla.Fields(0) usa lineas_tiquets, no tabla3; el tramo final L394 sí usa tabla3.Fields(0)",
            "Segundo bucle: AddNew LINEAS (TIENDA, CAJA original no remapeada, FECHA, ID_TIQUETL, CODIGO, hora) si Seek no encuentra"
        ],
        [
            "\\\\pedidos\\c\\programas\\prv_final\\prv_final_{MES}_{ANO}.mdb",
            "zetas.mdb"
        ],
        [
            "On Error GoTo errores → MsgBox \"Error tienda \" & tabla2!TIENDA; Resume sigueerror",
            "On Error GoTo 0 tras MoveLast/MoveFirst inicial"
        ]);

    private static AccessProcedureOverlay Creditos() => new(
        "Inserta créditos de QueryDef lineas en creditos y suma zetas.CREDITOS. avisos si caja contada/cerrada o cliente 0; la variable aceptado no impide la escritura.",
        ["OPERADOR", "lineas", "creditos índice identi", "zetas índice zeta"],
        [
            ZetasPath,
            "credito no nulo y <> 0",
            "Seek creditos (TIENDA, FECHA, id_client, CAJA, ID_TIQUETL): si ya existe, no vuelve a escribir",
            "id_client 4500-15000 o >19999: log/MsgBox si caja bloqueada (contada no cerrada) o comprobado<>0 (cerrada)",
            "id_client=0: log 'CREDITO A CLIENTE ELIMINADO'",
            "Comentario explícito: los créditos entran todos aunque la caja esté contada/cerrada",
            "aceptado=False cuando caja cerrada, pero nunca se consulta para saltar AddNew",
            CajaRemap
        ],
        ["lineas", "creditos", "zetas"],
        ["zetas.CREDITOS", "creditos"],
        ["lineas"],
        [
            "zetas.CREDITOS += credito (Edit) o AddNew TIPO=Z ARQUEO=0 CREDITOS=credito",
            "creditos: TIENDA, FECHA, cliente=id_client, identi=ID_TIQUETL, credito (sin descuento; el /100*(100-por_credito) está comentado), CAJA remapeada"
        ],
        ["C:\\programas\\transmisiones\\log_*.txt y logS_*.txt"],
        ["Sin On Error global; MsgBox en créditos a caja cerrada/contada"]);

    private static AccessProcedureOverlay Arqueo() => new(
        "Ingesta de ficheros 20*_02_*.txt (arqueo) con companion 03: filtra líneas |X|, antepone TIENDA, llama DECLARADO y registra en fichero.",
        ["OPERADOR", "d_red", "h_red", "fichero"],
        [
            ZetasPath,
            "Seek fichero: si ya está, GoTo 30",
            "Companion tipo 03 con fallback fecha ±1 si Len>25; si no GoTo 30",
            "Omite líneas con InStr |X|"
        ],
        ["fichero"],
        ["fichero", "zetas vía DECLARADO"],
        ["arque (en DECLARADO)"],
        ["Prefijo TIENDA| en a2.txt y a3.txt", "Tipo 02→a0/a2, tipo 03→a1/a3"],
        [
            "Lee d_red 20*_02_*.txt y companion 03",
            "Escribe h_red a0.txt, a1.txt, a2.txt, a3.txt"
        ],
        ["On Error GoTo 20 Resume 30: no AddNew si falla companion/FileCopy"]);

    private static AccessProcedureOverlay Declarado() => new(
        "Suma totalpropia de QueryDef arque sobre zetas.DECLARADO y contado; rellena ARQUEO=n_zeta si estaba a 0. OpenRecordset('arque') va contra BD (zetas.mdb), no CurrentDb.",
        ["OPERADOR", "QueryDef arque resuelta en zetas.mdb (pendiente confirmar si existe allí o se usa la de IMPORTAR)", "zetas"],
        [
            ZetasPath,
            "totalpropia no nulo y <> 0",
            "Seek zetas (TIENDA, FECHA, TIPO de arque, CAJA remapeada)",
            "bloqueado → GoTo 30",
            CajaRemap
        ],
        ["arque", "zetas"],
        ["zetas.DECLARADO", "zetas.contado", "zetas.ARQUEO"],
        ["arque"],
        [
            "DECLARADO += totalpropia; contado += totalpropia",
            "Si ARQUEO existente = 0, ARQUEO = n_zeta",
            "Alta: TIPO = tabla!TIPO (no forzado a Z)"
        ],
        ["zetas.mdb"],
        ["GoTo 30 si bloqueado; sin On Error explícito"]);

    private static AccessProcedureOverlay Pagos() => new(
        "Ingesta 20*_06_*.txt: copia a a0, antepone TIENDA en a1, llama pagos1 y registra fichero.",
        ["OPERADOR", "d_red", "h_red", "fichero"],
        [ZetasPath, "Seek fichero: match → GoTo 30"],
        ["fichero"],
        ["fichero", "zetas/pagos vía pagos1"],
        ["paguitos (en pagos1)"],
        ["TIENDA|línea en a1.txt"],
        ["Lee d_red 20*_06_*.txt", "Escribe h_red a0.txt y a1.txt"],
        ["Etiqueta 20 Resume 30 existe pero On Error GoTo 20 no está activo en el cuerpo (queda muerto)"]);

    private static AccessProcedureOverlay Pagos1() => new(
        "Suma importes de paguitos a zetas.PAGOS y añade filas en pagos. Omite caja bloqueada.",
        ["OPERADOR", "paguitos", "pagos índice tienda", "zetas"],
        [
            ZetasPath,
            "PAGOS no nulo y <> 0",
            "bloqueado → log y GoTo 30",
            "Seek pagos está comentado: siempre AddNew",
            CajaRemap
        ],
        ["paguitos", "zetas", "pagos"],
        ["zetas.PAGOS", "pagos"],
        ["paguitos"],
        [
            "zetas.PAGOS += paguitos.PAGOS; ARQUEO=n_zeta si ARQUEO era 0",
            "pagos.AddNew: TIENDA, FECHA, CAJA, pago, concepto, vendedor"
        ],
        ["log_*.txt / logS_*.txt si bloqueado"],
        ["On Error GoTo 30 alrededor de IsNull(PAGOS)", "On Error GoTo 21 en Update/MoveNext (sale de la función)"]);

    private static AccessProcedureOverlay Cobros() => new(
        "Igual que PAGOS pero patrón 20*_00_*.txt y Call cobros1.",
        ["OPERADOR", "d_red", "h_red", "fichero"],
        [ZetasPath, "Seek fichero: match → GoTo 30"],
        ["fichero"],
        ["fichero", "zetas/pagos vía cobros1"],
        ["paguitos0 (en cobros1)"],
        ["TIENDA|línea en a1.txt"],
        ["Lee d_red 20*_00_*.txt", "Escribe h_red a0.txt y a1.txt"],
        ["20 Resume 30 presente y no enganchado (igual que PAGOS)"]);

    private static AccessProcedureOverlay Cobros1() => new(
        "Como pagos1 pero el importe se niega (cobro = pago * -1) leyendo paguitos0.",
        ["OPERADOR", "paguitos0", "pagos", "zetas"],
        [ZetasPath, "PAGOS no nulo y <> 0", "bloqueado → log y GoTo 30", CajaRemap],
        ["paguitos0", "zetas", "pagos"],
        ["zetas.PAGOS", "pagos"],
        ["paguitos0"],
        [
            "zetas.PAGOS += (paguitos0.PAGOS * -1)",
            "pagos.pago = PAGOS * -1"
        ],
        ["log_*.txt / logS_*.txt"],
        ["GoTo 30 si bloqueado; sin On Error GoTo 21"]);

    private static AccessProcedureOverlay Monedas() => new(
        "Mueve/convierte ficheros *_04_*.* de dd_red\\INBOX hacia h_red y llama Importar_MONEDAS. Si el destino ya existe, Kill del origen y reintenta.",
        ["dd_red (Form_Open: \\\\pedidos\\c\\tpvision\\)", "h_red", "d_red (solo en línea Shell cdados)"],
        [
            "Dir dd_red\\INBOX\\*_04_*.*",
            "Si ya existe h_red & Right(FIC, Len-3) & \"txt\" → Kill origen, MsgBox, GoTo 21",
            "Extensión IPZ → Shell cdados.EXE; si no, FileCopy a h_red",
            "Tras copiar/convertir, Kill el origen en INBOX si sigue existiendo"
        ],
        [],
        [],
        [],
        [
            "Val(Mid(FIC, 15, InStr('_')-15)) se usa solo para Echo (número de arqueo); pendiente si el layout del nombre IPZ coincide",
            "IPZ: Shell \"C:\\tpvision\\cdados.EXE D {INBOX} {FIC} {d_red} TXT\" vbHide (no ejecutado en este análisis)"
        ],
        [
            "Lee dd_red\\INBOX\\*_04_*.*",
            "Escribe/copia a h_red\\{FIC} o conversión TXT vía cdados hacia d_red",
            "Elimina origen INBOX",
            "C:\\tpvision\\cdados.EXE"
        ],
        ["MsgBox si destino ya existe", "GoTo 21 reinicia el Dir"]);

    private static AccessProcedureOverlay ImportarMonedas() => new(
        "Ingesta h_red 20*_04_*.txt con companion 03 en d_red: antepone TIENDA en MONEDAS.txt y a3.txt, llama GRABAR_MONEDAS y registra fichero.",
        ["OPERADOR", "h_red", "d_red", "fichero"],
        [
            ZetasPath,
            "Tope 1000 ficheros: MsgBox y Exit Function (los ya listados no se procesan)",
            "Seek fichero: match → GoTo 30",
            "Companion 03 con fecha ±1"
        ],
        ["fichero"],
        ["fichero", "MONEDAS vía GRABAR_MONEDAS"],
        ["moneditas (en GRABAR_MONEDAS)"],
        ["TIENDA|línea"],
        [
            "Lee h_red 20*_04_*.txt y d_red companion 03",
            "Escribe h_red a0.txt, a1.txt, MONEDAS.txt, a3.txt"
        ],
        ["On Error GoTo 20 Resume 30 si falla companion"]);

    private static AccessProcedureOverlay GrabarMonedas() => new(
        "Acumula cantidades de moneditas en monedas.mdb (tabla MONEDAS). Omite id_divisa=10 (tarjeta propia) y cajas bloqueadas. POR=0.00601 si id_divisa=2.",
        ["OPERADOR", "moneditas", "monedas.mdb: MONEDAS, i_divisas_MONEDAS, i_divisas", "zetas"],
        [
            "monedas.mdb: supervisores → c:\\programas\\transmisiones\\monedas.mdb; si no \\\\supervisores\\...\\MONEDAS.mdb",
            "id_divisa <> 10",
            "Si falta fila en i_divisas o i_divisas_MONEDAS → GoTo mal (no inserta)",
            "zetas.bloqueado → GoTo 30 (sale del While, no solo de la fila)",
            CajaRemap
        ],
        ["moneditas", "zetas", "MONEDAS", "i_divisas", "i_divisas_MONEDAS"],
        ["MONEDAS.CANTIDAD"],
        ["moneditas"],
        [
            "Seek MONEDAS (TIENDA, FECHA, CAJA, id_divisa, Valor): suma CANTIDAD o AddNew con nom_divisa/NOM_MONEDA",
            "POR = 0.00601 si id_divisa=2 else 1"
        ],
        ["zetas.mdb", "monedas.mdb / MONEDAS.mdb"],
        ["GoTo 30 aborta el resto del recordset si una caja está bloqueada", "GoTo mal salta Update de esa fila"]);

    private static AccessProcedureOverlay MoverFicheros() => new(
        "Archiva o borra ficheros de tiquets 07 ya registrados y con fecha < Date-3; borra otros patrones 03/VEN/PED/10/11/CLI/log/tiq_; copia backups de MDB. No registra fichero.",
        [
            "OPERADOR → ddd_red C:\\d\\tpvision\\received\\ vs \\\\supervisores\\d\\tpvision\\received\\",
            "d_red, h_red",
            "fichero (solo Seek para decidir qué 07 archivar)",
            "FECHA = Date - 3; FECHon yyyymmdd"
        ],
        [
            "Solo 20*_07_*.txt con Left(nombre,8) < FECHon y presentes en fichero se mueven",
            "Carpeta mesano = NombreMes & yyyy & dígito día; sufijo \"2\" si día >4 o =1",
            "20*_03_*.txt antiguos: Kill en d_red y h_red (Name a archivo está comentado)",
            "VEN*.txt, PED*.txt, 20*_11_.txt, 20*_10_.txt, CLI*.txt, *.log, tiq_*.txt: Kill en d_red si fecha < FECHon"
        ],
        ["fichero"],
        [],
        [],
        [],
        [
            "Mueve (Name) d_red y h_red 07* al archivo ddd_red\\mesano[2]\\; Kill destino previo si existe",
            "Kill d_red 03 y otros patrones",
            "FileCopy backups: pagos.mdb→c:\\D\\copia pedidos\\pagos.sav; zetas.mdb→\\\\Estadisticas\\...\\zetas.sav; monedas.mdb→monedas.sav; PEDIDO_ESTAD.MDB; transmisiones.mdb→transmisiones.sav",
            "Kill destino .sav si ya existía"
        ],
        ["On Error GoTo 0 tras listar 07", "No hay handler alrededor de FileCopy/Kill de backups"]);

    private static AccessProcedureOverlay PrvFinales() => new(
        "Consolida venta_clientes3 mensuales hacia prv_final_{mes}_{ano}.mdb y pedidos/contador. Recorre meses Date-9..Date-5. SQL histórico masivo está comentado; rigen las dos SELECT vivas y crea_fic.",
        [
            "C:\\programas\\estadisticas\\temporal.mdb formates",
            "\\\\pedidos\\c\\programas\\transmisiones\\transmisiones.mdb",
            "\\\\pedidos\\c\\programas\\transmisiones\\venta_clientes3_{mes}_{ano}.mdb",
            "fecha_importar.txt → fecha_nueva",
            "contador, pedidos (CurrentDb)",
            "consumos.mdb (abierto; usos posteriores mayormente comentados)"
        ],
        [
            "Si falta venta_clientes3_{mes}_{ano}.mdb → GoTo 100 (siguiente mes)",
            "tabla5: fecha > 23-08-2017 And (tienda=-1 Or fecha<=fecha_nueva) And id_client not between -10000 and -10989 And fecha < Date-5 And tienda<>33 And tienda<200 And tienda<>163 And tienda<>162; tienda 150 BARRA/MESAS-TERR se ve como 149 (tienda2)",
            "TABLA55: id_client between 10000 and 10989 And fecha < Date And fecha > 17-09-2020 And tienda<>33 And tienda<200",
            "On Error Resume Next antes de esas SELECT"
        ],
        ["formates", "venta_clientes3 (MDB externo)", "contador", "pedidos", "prv_final", "LINEAS", "encargo (SQL max cobrado)", "consumos (comentarios)"],
        ["prv_final", "pedidos", "contador", "fecha_importar.txt si no existe"],
        [],
        [
            "IIf tienda=150 y seccion_sala BARRA|MESAS-TERR → tienda2=149",
            "crea_fic(tienda, fecha) suma ventas filtradas",
            "TABLA55 escribe prv_final con TIENDA+1000 y pedidos con TIENDA = id_client-10000; omite pedidos si TIENDA 149|150",
            "Descuento crédito pre 28-09-2017 y TIENDA<155 para id_client 0-4500 (fragmento vivo junto a CANTIDAD)"
        ],
        [
            "\\\\pedidos\\c\\programas\\prv_final\\prv_final_{mes}_{ano}.mdb (CreateDatabase si falta)",
            "\\\\pedidos\\c\\programas\\transmisiones\\fecha_importar.txt (Input o crea con 01/01/2001)",
            "\\\\pedidos\\c\\programas\\transmisiones\\consumos.mdb",
            "C:\\programas\\estadisticas\\temporal.mdb"
        ],
        ["On Error Resume Next en las SELECT vivas", "GoTo 100 si falta el MDB mensual"]);

    private static AccessProcedureOverlay CreaFic() => new(
        "Devuelve la suma de VENTA de venta_clientes3 para una tienda/fecha (N_ENCARGO=0), con filtros de código y cliente. DELETE ventas_seccion_sala99 y filtros_1; el INSERT a ventas_seccion_sala99 está comentado.",
        ["TIENDA", "FECHA", "bd5 (venta_clientes3 abierto por prv_finales_Click)"],
        [
            "TIENDA=149 → filas de tienda=150 BARRA|MESAS-TERR",
            "TIENDA=150 → tienda=150 excluyendo BARRA y MESAS-TERR",
            "Resto: tienda=TIENDA",
            "N_ENCARGO=0",
            "CODIGO <> 0 And <> -1 And <> 2; (22100 y 11027 solo si VENTA<>0); id_client <> \"4501\" y <> \"4502\"",
            "Suma si id_client 1-9999, o si <10000 o >10989 o 9001-9999 o Null/\"\", excepto id_client < 0"
        ],
        ["venta_clientes3", "ventas_seccion_sala99 (DELETE)", "FILTROS_1"],
        ["ventas_seccion_sala99 (DELETE; INSERT comentado)", "FILTROS_1"],
        [],
        ["crea_fic acumula tabla5!VENTA según filtros de cliente/código"],
        [],
        []);

    private static AccessProcedureOverlay Filtros1() => new(
        "Reemplaza la fila única de FILTROS_1 local con el rango de tienda/fecha/vendedor/artículo pedido.",
        ["desde_tienda", "hasta_tienda", "desde_fecha", "hasta_fecha", "desde_vendedor", "hasta_vendedor", "tipo_fecha", "d_art", "h_art"],
        ["crea_fic llama con (TIENDA, TIENDA, FECHA, FECHA, 0, 0, 2, 0, 0)"],
        ["FILTROS_1"],
        ["FILTROS_1"],
        [],
        ["DELETE FROM FILTROS_1; INSERT INTO FILTROS_1 VALUES(...)"],
        [],
        ["BeginTrans/CommitTrans alrededor de cada Execute"]);

    private static AccessProcedureOverlay ComprobarPrvFinal() => new(
        "Si no existe prv_final_{MES}_{ANO}.mdb lo crea cifrado con tablas LINEAS y prv_final e índices.",
        ["MES", "ANO"],
        ["Dir del MDB: si falta, CreateDatabase dbEncrypt"],
        [],
        ["LINEAS", "prv_final (creación de esquema)"],
        [],
        [
            "LINEAS: TIENDA Long, CAJA Long, FECHA Date, HORA Text 8, ID_TIQUETL Text 50, CODIGO Text 13; índice (TIENDA, CAJA, FECHA, hora, ID_TIQUETL, CODIGO)",
            "prv_final: VENTA, fecha, HORA, vendedor, tienda, id_tiquetl; índices tienda y tienda2"
        ],
        ["\\\\pedidos\\c\\programas\\prv_final\\prv_final_{MES}_{ANO}.mdb"],
        []);

    private static AccessProcedureOverlay GrabarTarjeta() => new(
        "Acumula IMPORTE_TARJETA en MONEDAS como id_divisa=10 Valor1=1. Omite caja bloqueada. Lo llama ventas_seccion cuando TARJETA<>0.",
        ["IMPORTE_TARJETA", "TIENDA", "FECHA", "CAJA", "monedas.mdb", "zetas"],
        ["zetas.bloqueado → GoTo 30", "Seek MONEDAS (TIENDA, FECHA, CAJA, 10, 1)", "Si faltan i_divisas id=10 o valor (10,1) → GoTo mal2", CajaRemap],
        ["zetas", "MONEDAS", "i_divisas", "i_divisas_MONEDAS"],
        ["MONEDAS.CANTIDAD"],
        [],
        ["CANTIDAD += IMPORTE_TARJETA o AddNew POR=1 id_divisa=10 Valor1=1"],
        ["zetas.mdb", "monedas.mdb"],
        ["GoTo 30 si bloqueado", "mal2: aún así ejecuta tabla.Update"]);

    private static AccessProcedureOverlay FormOpen() => new(
        "Inicializa rutas UNC, OPERADOR vía terminal, borra/recarga FORMATES desde dossis. No lo llama Importar_Click; es prerrequisito del formulario.",
        ["OPERADOR", "dossis"],
        ["While OPERADOR=\"\" Call terminal"],
        ["dossis", "FORMATES"],
        ["FORMATES"],
        [],
        ["DELETE FORMATES; INSERT SELECT * FROM dossis; UPDATE dosissol=1", "por_credito=10", "hay_incidencias=False"],
        [
            "dd_red=\\\\pedidos\\c\\tpvision\\",
            "d_red=\\\\pedidos\\c\\tpvision\\received\\received2\\",
            "h_red=\\\\pedidos\\c\\ipv\\received\\"
        ],
        ["On Error GoTo 20 está comentado"]);
}
