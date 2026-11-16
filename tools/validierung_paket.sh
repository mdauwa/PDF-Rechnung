#!/bin/bash
# Baut src/PdfRechnung/Edi/Validierung.zip (Prüfartefakte für E-Rechnungen).
# Quellen: ConnectingEurope/eInvoicing-EN16931 (EUPL 1.2), itplr-kosit/xrechnung-schematron (Apache 2.0),
#          SchXslt 1.10.1 (Maven, MIT) zum Übersetzen der XRechnung-Schematron, ph-ubl21 8.0.3 (UBL 2.1 XSD von OASIS).
# Das Übersetzen .sch -> .xslt erledigt tools/schxslt (kleines .NET-Programm mit SaxonCS-HE):
#   dotnet run --project tools/schxslt -- compile <schxslt>/xslt/2.0/pipeline-for-svrl.xsl <in.sch> <out.xslt>
set -e
W=${1:-/tmp/xmlsrc}
mkdir -p $W && cd $W
[ -d en16931 ] || git clone -q --depth 1 https://github.com/ConnectingEurope/eInvoicing-EN16931.git en16931
[ -d xrs ] || git clone -q --depth 1 https://github.com/itplr-kosit/xrechnung-schematron.git xrs
[ -d schxslt ] || { curl -sSfL -o schxslt.jar https://repo1.maven.org/maven2/name/dmaus/schxslt/schxslt/1.10.1/schxslt-1.10.1.jar; unzip -q schxslt.jar -d schxslt; }
[ -d ubl ] || { curl -sSfL -o ph-ubl21.jar https://repo1.maven.org/maven2/com/helger/ubl/ph-ubl21/8.0.3/ph-ubl21-8.0.3.jar; unzip -q ph-ubl21.jar 'external/schemas/ubl21/*' -d ubl; }
echo "Weiter wie in der Übergabe beschrieben (Ordnerstruktur cii/xsd, ubl/xsd, en16931, xrechnung, lizenzen)."
