using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace PelnaKsiegowosc.Infrastructure.Ksef;

/// <summary>
/// Podpis XAdES-BES otaczany (enveloped) dokumentu XML — uwierzytelnienie w KSeF certyfikatem kwalifikowanym,
/// pieczęcią lub certyfikatem KSeF (POST /auth/xades-signature). Algorytmy: RSA-SHA256 lub ECDSA-SHA256, skrót SHA-256,
/// kanonikalizacja wyłączna (exc-c14n). Zgodnie z dokumentacją MF „Podpis XAdES” (ksef-docs/auth/podpis-xades.md).
/// </summary>
public static class XadesSigner
{
    public const string XadesNs = "http://uri.etsi.org/01903/v1.3.2#";
    public const string AuthTokenNs = "http://ksef.mf.gov.pl/auth/token/2.0";

    /// <summary>Dokument AuthTokenRequest (kontekst: NIP; podmiot z certyfikatu albo odcisk certyfikatu).</summary>
    public static XmlDocument AuthTokenRequest(string challenge, string nip, bool byFingerprint)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml($"""<?xml version="1.0" encoding="utf-8"?><AuthTokenRequest xmlns="{AuthTokenNs}"><Challenge>{SecurityElementEscape(challenge)}</Challenge><ContextIdentifier><Nip>{SecurityElementEscape(nip)}</Nip></ContextIdentifier><SubjectIdentifierType>{(byFingerprint ? "certificateFingerprint" : "certificateSubject")}</SubjectIdentifierType></AuthTokenRequest>""");
        return doc;
    }

    private static string SecurityElementEscape(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    private sealed class XadesSignedXml : SignedXml
    {
        private readonly XmlElement _object;
        public XadesSignedXml(XmlDocument doc, XmlElement obj) : base(doc) => _object = obj;

        public override XmlElement? GetIdElement(XmlDocument? document, string idValue) =>
            base.GetIdElement(document, idValue) ?? _object.SelectSingleNode($"//*[@Id='{idValue}']") as XmlElement;
    }

    public static XmlDocument Sign(XmlDocument document, X509Certificate2 certificate, DateTimeOffset signingTime)
    {
        if (!certificate.HasPrivateKey) throw new InvalidOperationException("Certyfikat nie zawiera klucza prywatnego — wskaż plik .pfx/.p12 z kluczem.");
        var doc = (XmlDocument)document.Clone();
        doc.PreserveWhitespace = true;
        var sigId = "Signature-" + Guid.NewGuid().ToString("N");
        var propsId = "SignedProperties-" + Guid.NewGuid().ToString("N");

        // QualifyingProperties / SignedProperties (SigningTime, SigningCertificate)
        var ds = SignedXml.XmlDsigNamespaceUrl;
        var qp = doc.CreateElement("xades", "QualifyingProperties", XadesNs);
        qp.SetAttribute("Target", "#" + sigId);
        var sp = doc.CreateElement("xades", "SignedProperties", XadesNs);
        sp.SetAttribute("Id", propsId);
        var ssp = doc.CreateElement("xades", "SignedSignatureProperties", XadesNs);
        var st = doc.CreateElement("xades", "SigningTime", XadesNs);
        st.InnerText = signingTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'");
        ssp.AppendChild(st);
        var sc = doc.CreateElement("xades", "SigningCertificate", XadesNs);
        var cert = doc.CreateElement("xades", "Cert", XadesNs);
        var cd = doc.CreateElement("xades", "CertDigest", XadesNs);
        var dm = doc.CreateElement("ds", "DigestMethod", ds);
        dm.SetAttribute("Algorithm", SignedXml.XmlDsigSHA256Url);
        var dv = doc.CreateElement("ds", "DigestValue", ds);
        dv.InnerText = Convert.ToBase64String(SHA256.HashData(certificate.RawData));
        cd.AppendChild(dm);
        cd.AppendChild(dv);
        var iss = doc.CreateElement("xades", "IssuerSerial", XadesNs);
        var inm = doc.CreateElement("ds", "X509IssuerName", ds);
        inm.InnerText = certificate.Issuer;
        var isn = doc.CreateElement("ds", "X509SerialNumber", ds);
        isn.InnerText = new System.Numerics.BigInteger(Convert.FromHexString(certificate.SerialNumber), isUnsigned: true, isBigEndian: true).ToString();
        iss.AppendChild(inm);
        iss.AppendChild(isn);
        cert.AppendChild(cd);
        cert.AppendChild(iss);
        sc.AppendChild(cert);
        ssp.AppendChild(sc);
        sp.AppendChild(ssp);
        qp.AppendChild(sp);
        var obj = new DataObject { Data = new XmlNodeList_(qp) };

        var signed = new XadesSignedXml(doc, qp) { SigningKey = (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey() };
        signed.Signature.Id = sigId;
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = certificate.GetRSAPrivateKey() is not null ? SignedXml.XmlDsigRSASHA256Url : "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256";

        var docRef = new Reference("") { DigestMethod = SignedXml.XmlDsigSHA256Url };
        docRef.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        docRef.AddTransform(new XmlDsigExcC14NTransform());
        signed.AddReference(docRef);
        var propsRef = new Reference("#" + propsId) { DigestMethod = SignedXml.XmlDsigSHA256Url, Type = "http://uri.etsi.org/01903#SignedProperties" };
        propsRef.AddTransform(new XmlDsigExcC14NTransform());
        signed.AddReference(propsRef);
        signed.AddObject(obj);
        var ki = new KeyInfo();
        ki.AddClause(new KeyInfoX509Data(certificate));
        signed.KeyInfo = ki;
        signed.ComputeSignature();
        doc.DocumentElement!.AppendChild(doc.ImportNode(signed.GetXml(), true));
        return doc;
    }

    /// <summary>Weryfikacja podpisu (kontrola własna przed wysłaniem i w testach).</summary>
    public static bool Verify(XmlDocument signedDoc)
    {
        var sigEl = (XmlElement)signedDoc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!;
        var qp = (XmlElement)signedDoc.GetElementsByTagName("QualifyingProperties", XadesNs)[0]!;
        var s = new XadesSignedXml(signedDoc, qp);
        s.LoadXml(sigEl);
        return s.CheckSignature();
    }

    private sealed class XmlNodeList_ : XmlNodeList
    {
        private readonly XmlNode _node;
        public XmlNodeList_(XmlNode node) => _node = node;
        public override int Count => 1;
        public override XmlNode? Item(int index) => index == 0 ? _node : null;
        public override System.Collections.IEnumerator GetEnumerator() { yield return _node; }
    }
}
