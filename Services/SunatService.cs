using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SistemaChotaExpress.Models;

namespace SistemaChotaExpress.Services
{
    public class SunatEnvioResultado
    {
        public bool Exito { get; set; }
        public string Mensaje { get; set; } = string.Empty;
        public string? CodigoRespuesta { get; set; }
        public string? XmlPath { get; set; }
        public string? ZipPath { get; set; }
        public string? CdrPath { get; set; }
        public string? PdfUrl { get; set; }
        public string? CdrUrl { get; set; }
        public string? DigestValue { get; set; }
        public string? CodigoHash { get; set; }
    }

    /// <summary>
    /// Servicio de facturación electrónica directa a SUNAT (Costo S/ 0.00).
    /// Genera XML estándar UBL 2.1, firma digitalmente con el Certificado Digital Tributario (.pfx),
    /// empaqueta en .ZIP y transmite directamente vía Web Service SOAP (sendBill) a SUNAT (Beta o Producción),
    /// extrayendo y validando el CDR (Constancia de Recepción).
    /// </summary>
    public class SunatService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<SunatService> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly IHttpClientFactory _httpClientFactory;

        public SunatService(
            IConfiguration config,
            ILogger<SunatService> logger,
            IWebHostEnvironment env,
            IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _logger = logger;
            _env = env;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Procesa la venta: genera el XML UBL 2.1, lo firma digitalmente y lo transmite directamente a SUNAT.
        /// </summary>
        public async Task<SunatEnvioResultado> ProcesarComprobanteAsync(Venta venta)
        {
            var resultado = new SunatEnvioResultado();

            try
            {
                bool conexionDirecta = _config.GetValue<bool>("Sunat:ConexionDirecta", true);

                if (conexionDirecta)
                {
                    _logger.LogInformation("Iniciando transmisión DIRECTA a SUNAT (Costo S/ 0.00) para venta #{VentaId}...", venta.Id_Venta);
                    return await EnviarDirectoASunatAsync(venta);
                }

                // Fallback opcional a Nubefact si el usuario configuró token expresamente
                string? nubefactUrl = _config["Sunat:NubefactUrl"];
                string? nubefactToken = _config["Sunat:NubefactToken"];

                if (!string.IsNullOrWhiteSpace(nubefactUrl) && !string.IsNullOrWhiteSpace(nubefactToken))
                {
                    _logger.LogInformation("Enviando comprobante #{VentaId} a SUNAT vía OSE/Nubefact...", venta.Id_Venta);
                    return await EnviarANubefactAsync(venta, nubefactUrl, nubefactToken);
                }

                // Guardar XML local si no hay conexión activa
                resultado.XmlPath = await GenerarYGuardarXmlAsync(venta);
                resultado.Exito = true;
                resultado.Mensaje = "XML UBL 2.1 generado y guardado localmente.";
                return resultado;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al procesar comprobante electrónico de venta {VentaId}", venta.Id_Venta);
                resultado.Exito = false;
                resultado.Mensaje = $"Error en procesamiento SUNAT: {ex.Message}";
                return resultado;
            }
        }

        /// <summary>
        /// Flujo completo directo a SUNAT: UBL 2.1 -> Firma Digital -> ZIP -> SOAP sendBill -> CDR.
        /// </summary>
        public async Task<SunatEnvioResultado> EnviarDirectoASunatAsync(Venta venta)
        {
            var resultado = new SunatEnvioResultado();

            string modo = _config["Sunat:Modo"] ?? "Beta";
            bool esBeta = modo.Equals("Beta", StringComparison.OrdinalIgnoreCase);

            string rucEmisor = _config["Sunat:Ruc"] ?? "20600308883";
            string usuarioSol = _config["Sunat:UsuarioSol"] ?? (esBeta ? "MODDATOS" : "FACTURADOR");
            string claveSol = _config["Sunat:ClaveSol"] ?? (esBeta ? "moddatos" : "");

            string endpoint = esBeta
                ? (_config["Sunat:BetaEndpoint"] ?? "https://e-beta.sunat.gob.pe/ol-ti-itcpfegem-beta/billService")
                : (_config["Sunat:ProduccionEndpoint"] ?? "https://e-factura.sunat.gob.pe/ol-ti-itcpfegem/billService");

            bool esFactura = venta.TipoComprobante == "Factura";
            string tipoDoc = esFactura ? "01" : "03";
            string serie = esFactura ? "F001" : "B001";
            string correlativo = venta.Id_Venta.ToString("D8");
            string nombreBase = $"{rucEmisor}-{tipoDoc}-{serie}-{correlativo}";

            // 1. Generar XML UBL 2.1 base
            string xmlOriginal = GenerarXmlUbl21(venta);

            // 2. Firmar digitalmente con el certificado
            var (xmlFirmado, digestValue) = FirmarXmlConCertificado(xmlOriginal);
            resultado.DigestValue = digestValue;
            resultado.CodigoHash = digestValue;

            // 3. Guardar XML firmado en disco
            string comprobantesDir = ObtenerDirectorio("Sunat:XmlOutputPath", "App_Data/Comprobantes");
            string xmlPath = Path.Combine(comprobantesDir, $"{nombreBase}.xml");
            await File.WriteAllTextAsync(xmlPath, xmlFirmado, Encoding.UTF8);
            resultado.XmlPath = xmlPath;

            // 4. Crear archivo ZIP que contiene el XML firmado
            byte[] zipBytes;
            using (var memoryStream = new MemoryStream())
            {
                using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                {
                    var zipEntry = archive.CreateEntry($"{nombreBase}.xml", CompressionLevel.Optimal);
                    using var entryStream = zipEntry.Open();
                    byte[] xmlBytes = Encoding.UTF8.GetBytes(xmlFirmado);
                    await entryStream.WriteAsync(xmlBytes, 0, xmlBytes.Length);
                }
                zipBytes = memoryStream.ToArray();
            }

            string zipPath = Path.Combine(comprobantesDir, $"{nombreBase}.zip");
            await File.WriteAllBytesAsync(zipPath, zipBytes);
            resultado.ZipPath = zipPath;

            // 5. Enviar por SOAP a SUNAT
            string zipBase64 = Convert.ToBase64String(zipBytes);
            string soapEnvelope = ConstruirSoapEnvelopeSendBill(rucEmisor, usuarioSol, claveSol, $"{nombreBase}.zip", zipBase64);

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(25);

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml")
            };
            request.Headers.Add("SOAPAction", "sendBill");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Timeout o error de conexión con servidor SUNAT ({Endpoint})", endpoint);
                resultado.Exito = true;
                resultado.Mensaje = $"Comprobante #{correlativo} guardado y firmado localmente. En espera de reintento de envío a SUNAT ({ex.Message}).";
                return resultado;
            }

            string responseBody = await response.Content.ReadAsStringAsync();

            // 6. Procesar respuesta SOAP
            if (response.IsSuccessStatusCode && responseBody.Contains("applicationResponse"))
            {
                resultado = ProcesarRespuestaCdr(responseBody, nombreBase, resultado);
            }
            else
            {
                resultado = ProcesarFalloSoap(responseBody, resultado);
            }

            return resultado;
        }

        /// <summary>
        /// Firma el documento XML UBL 2.1 utilizando el certificado digital (.pfx).
        /// Si aún no se ha subido un certificado de producción, genera una firma de desarrollo válida para pruebas Beta.
        /// </summary>
        public (string XmlFirmado, string DigestValue) FirmarXmlConCertificado(string xmlString)
        {
            var xmlDoc = new XmlDocument { PreserveWhitespace = true };
            xmlDoc.LoadXml(xmlString);

            X509Certificate2? cert = ObtenerCertificadoDigital();

            if (cert == null)
            {
                // En modo desarrollo / prueba sin certificado físico, generamos un certificado RSA temporal
                using var rsa = RSA.Create(2048);
                var req = new CertificateRequest("cn=TOURS CHOTA EXPRESS S.A.C.", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(3));
            }

            var signedXml = new SignedXml(xmlDoc)
            {
                SigningKey = cert.GetRSAPrivateKey()
            };

            var reference = new Reference
            {
                Uri = "",
                DigestMethod = "http://www.w3.org/2001/04/xmlenc#sha256"
            };

            var env = new XmlDsigEnvelopedSignatureTransform();
            reference.AddTransform(env);
            signedXml.AddReference(reference);

            var keyInfo = new KeyInfo();
            keyInfo.AddClause(new KeyInfoX509Data(cert));
            signedXml.KeyInfo = keyInfo;

            signedXml.ComputeSignature();

            XmlElement xmlDigitalSignature = signedXml.GetXml();

            // Insertar firma en <ext:ExtensionContent>
            var nsManager = new XmlNamespaceManager(xmlDoc.NameTable);
            nsManager.AddNamespace("ext", "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2");
            var extensionContent = xmlDoc.SelectSingleNode("//ext:ExtensionContent", nsManager);

            if (extensionContent != null)
            {
                extensionContent.RemoveAll();
                extensionContent.AppendChild(xmlDoc.ImportNode(xmlDigitalSignature, true));
            }
            else
            {
                xmlDoc.DocumentElement?.AppendChild(xmlDoc.ImportNode(xmlDigitalSignature, true));
            }

            string digestValue = "";
            var digestNode = xmlDigitalSignature.GetElementsByTagName("DigestValue");
            if (digestNode.Count > 0)
            {
                digestValue = digestNode[0]?.InnerText ?? "";
            }

            return (xmlDoc.OuterXml, digestValue);
        }

        private X509Certificate2? ObtenerCertificadoDigital()
        {
            try
            {
                string? certPath = _config["Sunat:CertificadoPath"];
                string? certPass = _config["Sunat:CertificadoPassword"];

                if (!string.IsNullOrWhiteSpace(certPath))
                {
                    string fullPath = Path.IsPathRooted(certPath)
                        ? certPath
                        : Path.Combine(_env.ContentRootPath, certPath);

                    if (File.Exists(fullPath))
                    {
                        return new X509Certificate2(fullPath, certPass, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("No se pudo cargar certificado físico SUNAT: {Message}", ex.Message);
            }

            return null;
        }

        private string ConstruirSoapEnvelopeSendBill(string ruc, string usuario, string clave, string zipFileName, string base64Zip)
        {
            return $@"<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:ser=""http://service.sunat.gob.pe"" xmlns:wsse=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"">
  <soapenv:Header>
    <wsse:Security>
      <wsse:UsernameToken>
        <wsse:Username>{ruc}{usuario}</wsse:Username>
        <wsse:Password>{clave}</wsse:Password>
      </wsse:UsernameToken>
    </wsse:Security>
  </soapenv:Header>
  <soapenv:Body>
    <ser:sendBill>
      <fileName>{zipFileName}</fileName>
      <contentFile>{base64Zip}</contentFile>
    </ser:sendBill>
  </soapenv:Body>
</soapenv:Envelope>";
        }

        private SunatEnvioResultado ProcesarRespuestaCdr(string soapResponse, string nombreBase, SunatEnvioResultado resultado)
        {
            try
            {
                int startIndex = soapResponse.IndexOf("<applicationResponse>") + "<applicationResponse>".Length;
                int endIndex = soapResponse.IndexOf("</applicationResponse>");

                if (startIndex >= 0 && endIndex > startIndex)
                {
                    string cdrBase64 = soapResponse.Substring(startIndex, endIndex - startIndex);
                    byte[] cdrZipBytes = Convert.FromBase64String(cdrBase64);

                    string cdrDir = ObtenerDirectorio("Sunat:CdrOutputPath", "App_Data/Cdr");
                    string cdrZipPath = Path.Combine(cdrDir, $"R-{nombreBase}.zip");
                    File.WriteAllBytes(cdrZipPath, cdrZipBytes);
                    resultado.CdrPath = cdrZipPath;

                    // Descomprimir el XML de CDR para extraer el mensaje de SUNAT
                    using (var memoryStream = new MemoryStream(cdrZipBytes))
                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                            {
                                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                                string cdrXml = reader.ReadToEnd();

                                var cdrDoc = XDocument.Parse(cdrXml);
                                XNamespace cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
                                XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

                                string? codRespuesta = cdrDoc.Descendants(cbc + "ResponseCode").FirstOrDefault()?.Value;
                                string? descripcion = cdrDoc.Descendants(cbc + "Description").FirstOrDefault()?.Value;

                                resultado.CodigoRespuesta = codRespuesta ?? "0";
                                resultado.Exito = (codRespuesta == "0");
                                resultado.Mensaje = descripcion ?? "Comprobante aceptado por SUNAT (CDR recibido).";

                                string cdrXmlPath = Path.Combine(cdrDir, entry.Name);
                                File.WriteAllText(cdrXmlPath, cdrXml, Encoding.UTF8);
                                break;
                            }
                        }
                    }

                    _logger.LogInformation("CDR SUNAT procesado exitosamente para {NombreBase}: {Mensaje}", nombreBase, resultado.Mensaje);
                }
                else
                {
                    resultado.Exito = true;
                    resultado.Mensaje = "Comprobante enviado a SUNAT.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al descomprimir CDR de respuesta SUNAT");
                resultado.Exito = true;
                resultado.Mensaje = "Comprobante enviado. CDR recibido pendiente de descompresión.";
            }

            return resultado;
        }

        private SunatEnvioResultado ProcesarFalloSoap(string soapResponse, SunatEnvioResultado resultado)
        {
            resultado.Exito = false;
            try
            {
                if (soapResponse.Contains("<faultstring>"))
                {
                    int start = soapResponse.IndexOf("<faultstring>") + "<faultstring>".Length;
                    int end = soapResponse.IndexOf("</faultstring>");
                    resultado.Mensaje = "Respuesta SUNAT: " + soapResponse.Substring(start, end - start);
                }
                else
                {
                    resultado.Mensaje = "Error en comunicación con SUNAT: " + soapResponse;
                }
            }
            catch
            {
                resultado.Mensaje = "Error de respuesta del servidor SUNAT.";
            }

            _logger.LogWarning("SUNAT SOAP Error: {Mensaje}", resultado.Mensaje);
            return resultado;
        }

        private string ObtenerDirectorio(string configKey, string defaultRelPath)
        {
            string? dir = _config[configKey];
            string fullPath = string.IsNullOrWhiteSpace(dir)
                ? Path.Combine(_env.ContentRootPath, defaultRelPath)
                : (Path.IsPathRooted(dir) ? dir : Path.Combine(_env.ContentRootPath, dir));

            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }

            return fullPath;
        }

        /// <summary>
        /// Genera el XML UBL 2.1 de la venta y lo guarda en App_Data/Comprobantes/.
        /// </summary>
        public async Task<string?> GenerarYGuardarXmlAsync(Venta venta)
        {
            try
            {
                string xmlContent = GenerarXmlUbl21(venta);
                string outputPath = ObtenerDirectorio("Sunat:XmlOutputPath", "App_Data/Comprobantes");

                bool esFactura = venta.TipoComprobante == "Factura";
                string serie = esFactura ? "F001" : "B001";
                string tipoDoc = esFactura ? "01" : "03";
                string correlativo = venta.Id_Venta.ToString("D8");
                string ruc = _config["Sunat:Ruc"] ?? "20600308883";

                string fileName = $"{ruc}-{tipoDoc}-{serie}-{correlativo}.xml";
                string filePath = Path.Combine(outputPath, fileName);

                await File.WriteAllTextAsync(filePath, xmlContent, Encoding.UTF8);
                _logger.LogInformation("XML SUNAT guardado: {FilePath}", filePath);

                return filePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al guardar XML SUNAT para venta {VentaId}", venta.Id_Venta);
                return null;
            }
        }

        /// <summary>
        /// Genera el contenido XML UBL 2.1 SUNAT para una venta.
        /// </summary>
        public string GenerarXmlUbl21(Venta venta)
        {
            bool esFactura = venta.TipoComprobante == "Factura";
            string serie = esFactura ? "F001" : "B001";
            string tipoDocSunat = esFactura ? "01" : "03";
            string numeroCorrelativo = venta.Id_Venta.ToString("D8");
            string idComprobante = $"{serie}-{numeroCorrelativo}";
            string rucEmisor = _config["Sunat:Ruc"] ?? "20600308883";
            string razonSocialEmisor = _config["Sunat:RazonSocial"] ?? "TOURS CHOTA EXPRESS S.A.C.";

            decimal subtotal = Math.Round(venta.PrecioPagado / 1.18m, 2);
            decimal igv = venta.PrecioPagado - subtotal;

            string docReceptor = esFactura
                ? (venta.RucEmpresa ?? "20000000001")
                : (venta.ObjetoPasajero?.NumeroDocumento ?? "00000000");
            string tipoDocReceptor = esFactura ? "6" : (venta.ObjetoPasajero?.TipoDocumento == "DNI" ? "1" : "4");
            string razonReceptor = esFactura
                ? (venta.RazonSocialEmpresa ?? "EMPRESA CLIENTE")
                : (venta.ObjetoPasajero?.NombreCompleto ?? "CLIENTE GENERAL");

            string descripcionServicio = $"SERVICIO DE TRANSPORTE DE PASAJEROS: " +
                $"{(venta.ObjetoViaje?.ObjetoRuta?.Origen ?? "ORIGEN")} A " +
                $"{(venta.ObjetoViaje?.ObjetoRuta?.Destino ?? "DESTINO")} - " +
                $"ASIENTO {venta.NumeroAsiento} - " +
                $"BUS {(venta.ObjetoViaje?.ObjetoBus?.Placa ?? venta.ObjetoViaje?.PlacaVehiculo ?? "S/P")}";

            XNamespace cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
            XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
            XNamespace ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";

            var xmlDoc = new XDocument(
                new XDeclaration("1.0", "UTF-8", "no"),
                new XElement("Invoice",
                    new XAttribute(XNamespace.Xmlns + "cac", cac.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "cbc", cbc.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "ext", ext.NamespaceName),
                    new XElement(ext + "UBLExtensions",
                        new XElement(ext + "UBLExtension",
                            new XElement(ext + "ExtensionContent")
                        )
                    ),
                    new XElement(cbc + "UBLVersionID", "2.1"),
                    new XElement(cbc + "CustomizationID", "2.0"),
                    new XElement(cbc + "ID", idComprobante),
                    new XElement(cbc + "IssueDate", venta.FechaVenta.ToString("yyyy-MM-dd")),
                    new XElement(cbc + "IssueTime", venta.FechaVenta.ToString("HH:mm:ss")),
                    new XElement(cbc + "InvoiceTypeCode", new XAttribute("listID", "0101"), tipoDocSunat),
                    new XElement(cbc + "DocumentCurrencyCode", "PEN"),

                    // Emisor
                    new XElement(cac + "AccountingSupplierParty",
                        new XElement(cac + "Party",
                            new XElement(cac + "PartyIdentification",
                                new XElement(cbc + "ID", new XAttribute("schemeID", "6"), rucEmisor)
                            ),
                            new XElement(cac + "PartyLegalEntity",
                                new XElement(cbc + "RegistrationName", razonSocialEmisor)
                            )
                        )
                    ),

                    // Receptor
                    new XElement(cac + "AccountingCustomerParty",
                        new XElement(cac + "Party",
                            new XElement(cac + "PartyIdentification",
                                new XElement(cbc + "ID", new XAttribute("schemeID", tipoDocReceptor), docReceptor)
                            ),
                            new XElement(cac + "PartyLegalEntity",
                                new XElement(cbc + "RegistrationName", razonReceptor)
                            )
                        )
                    ),

                    // Impuestos
                    new XElement(cac + "TaxTotal",
                        new XElement(cbc + "TaxAmount", new XAttribute("currencyID", "PEN"), igv.ToString("0.00")),
                        new XElement(cac + "TaxSubtotal",
                            new XElement(cbc + "TaxableAmount", new XAttribute("currencyID", "PEN"), subtotal.ToString("0.00")),
                            new XElement(cbc + "TaxAmount", new XAttribute("currencyID", "PEN"), igv.ToString("0.00")),
                            new XElement(cac + "TaxCategory",
                                new XElement(cac + "TaxScheme",
                                    new XElement(cbc + "ID", "1000"),
                                    new XElement(cbc + "Name", "IGV"),
                                    new XElement(cbc + "TaxTypeCode", "VAT")
                                )
                            )
                        )
                    ),

                    // Totales monetarios
                    new XElement(cac + "LegalMonetaryTotal",
                        new XElement(cbc + "LineExtensionAmount", new XAttribute("currencyID", "PEN"), subtotal.ToString("0.00")),
                        new XElement(cbc + "TaxInclusiveAmount", new XAttribute("currencyID", "PEN"), venta.PrecioPagado.ToString("0.00")),
                        new XElement(cbc + "PayableAmount", new XAttribute("currencyID", "PEN"), venta.PrecioPagado.ToString("0.00"))
                    ),

                    // Línea de detalle del pasaje
                    new XElement(cac + "InvoiceLine",
                        new XElement(cbc + "ID", "1"),
                        new XElement(cbc + "InvoicedQuantity", new XAttribute("unitCode", "ZZ"), "1"),
                        new XElement(cbc + "LineExtensionAmount", new XAttribute("currencyID", "PEN"), subtotal.ToString("0.00")),
                        new XElement(cac + "Item",
                            new XElement(cbc + "Description", descripcionServicio),
                            new XElement(cac + "SellersItemIdentification",
                                new XElement(cbc + "ID", "78111802")
                            )
                        ),
                        new XElement(cac + "Price",
                            new XElement(cbc + "PriceAmount", new XAttribute("currencyID", "PEN"), subtotal.ToString("0.00"))
                        )
                    )
                )
            );

            using var stream = new MemoryStream();
            xmlDoc.Save(stream);
            stream.Position = 0;
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private async Task<SunatEnvioResultado> EnviarANubefactAsync(Venta venta, string url, string token)
        {
            var resultado = new SunatEnvioResultado();
            try
            {
                bool esFactura = venta.TipoComprobante == "Factura";
                decimal subtotal = Math.Round(venta.PrecioPagado / 1.18m, 2);
                decimal igv = venta.PrecioPagado - subtotal;

                string clienteTipoDoc = esFactura ? "6" : (venta.ObjetoPasajero?.TipoDocumento == "DNI" ? "1" : "4");
                string clienteDoc = esFactura
                    ? (venta.RucEmpresa ?? "20000000001")
                    : (venta.ObjetoPasajero?.NumeroDocumento ?? "00000000");
                string clienteNombre = esFactura
                    ? (venta.RazonSocialEmpresa ?? "EMPRESA CLIENTE")
                    : (venta.ObjetoPasajero?.NombreCompleto ?? "CLIENTE GENERAL");
                string clienteDireccion = esFactura
                    ? (venta.DireccionEmpresa ?? "CHOTA - CAJAMARCA")
                    : "-";

                string descripcion = $"SERVICIO DE TRANSPORTE TERRESTRE DE PASAJEROS: " +
                    $"{(venta.ObjetoViaje?.ObjetoRuta?.Origen ?? "CHOTA").ToUpper()} A " +
                    $"{(venta.ObjetoViaje?.ObjetoRuta?.Destino ?? "CHICLAYO").ToUpper()} | " +
                    $"ASIENTO #{venta.NumeroAsiento} | " +
                    $"PASAJERO: {(venta.ObjetoPasajero?.NombreCompleto ?? "PASAJERO").ToUpper()}";

                var payload = new Dictionary<string, object>
                {
                    ["operacion"] = "generar_comprobante",
                    ["tipo_de_comprobante"] = esFactura ? 1 : 2,
                    ["serie"] = esFactura ? "F001" : "B001",
                    ["numero"] = venta.Id_Venta,
                    ["sunat_transaction"] = 1,
                    ["cliente_tipo_de_documento"] = clienteTipoDoc,
                    ["cliente_numero_de_documento"] = clienteDoc,
                    ["cliente_denominacion"] = clienteNombre,
                    ["cliente_direccion"] = clienteDireccion,
                    ["cliente_email"] = venta.ObjetoPasajero?.Correo ?? "",
                    ["fecha_de_emision"] = venta.FechaVenta.ToString("dd-MM-yyyy"),
                    ["moneda"] = 1,
                    ["tipo_de_cambio"] = "",
                    ["porcentaje_de_igv"] = 18.0,
                    ["total_gravada"] = subtotal,
                    ["total_inafecta"] = 0.0,
                    ["total_exonerada"] = 0.0,
                    ["total_igv"] = igv,
                    ["total"] = venta.PrecioPagado,
                    ["detraccion"] = false,
                    ["enviar_automaticamente_a_la_sunat"] = true,
                    ["enviar_automaticamente_al_cliente"] = false,
                    ["formato_de_pdf"] = "a4",
                    ["items"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["unidad_de_medida"] = "ZZ",
                            ["codigo"] = "78111802",
                            ["descripcion"] = descripcion,
                            ["cantidad"] = 1,
                            ["valor_unitario"] = subtotal,
                            ["precio_unitario"] = venta.PrecioPagado,
                            ["descuento"] = "",
                            ["subtotal"] = subtotal,
                            ["tipo_de_igv"] = 1,
                            ["igv"] = igv,
                            ["total"] = venta.PrecioPagado,
                            ["anticipo_regularizacion"] = false
                        }
                    }
                };

                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                client.Timeout = TimeSpan.FromSeconds(15);

                string jsonString = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(url, content);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    var root = doc.RootElement;

                    resultado.Exito = true;
                    resultado.Mensaje = root.TryGetProperty("sunat_description", out var desc)
                        ? desc.GetString() ?? "Comprobante aceptado por SUNAT"
                        : "Comprobante emitido con éxito";
                    resultado.PdfUrl = root.TryGetProperty("enlace_del_pdf", out var pdf) ? pdf.GetString() : null;
                    resultado.CdrUrl = root.TryGetProperty("enlace_del_cdr", out var cdr) ? cdr.GetString() : null;
                    resultado.DigestValue = root.TryGetProperty("codigo_hash", out var h) ? h.GetString() : null;
                }
                else
                {
                    resultado.Exito = false;
                    resultado.Mensaje = $"Error de respuesta ({response.StatusCode}): {responseBody}";
                }
            }
            catch (Exception ex)
            {
                resultado.Exito = false;
                resultado.Mensaje = $"Excepción al contactar OSE: {ex.Message}";
            }

            return resultado;
        }
    }
}
