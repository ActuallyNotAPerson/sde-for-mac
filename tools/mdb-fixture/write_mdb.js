// Writes the rows of an SDE Export.json into an existing Access (.mdb) database with Jackcess.
// Test tooling only: builds tests/fixtures/sde-install/Database/SDEDatabase.mdb from FEMA's empty database
// (see make_fixture.sh). Runs under Java 8's Nashorn: jjs -cp <jackcess jars> write_mdb.js -- Export.json target.mdb
var File = Java.type("java.io.File");
var Files = Java.type("java.nio.file.Files");
var Paths = Java.type("java.nio.file.Paths");
var JString = Java.type("java.lang.String");
var BigDecimal = Java.type("java.math.BigDecimal");
var LocalDateTime = Java.type("java.time.LocalDateTime");
var HashMap = Java.type("java.util.HashMap");
var JInteger = Java.type("java.lang.Integer"), JShort = Java.type("java.lang.Short"), JByte = Java.type("java.lang.Byte");
var JDouble = Java.type("java.lang.Double"), JFloat = Java.type("java.lang.Float");
var DatabaseBuilder = Java.type("com.healthmarketscience.jackcess.DatabaseBuilder");
var DateTimeType = Java.type("com.healthmarketscience.jackcess.DateTimeType");

var source = arguments[0], target = arguments[1];
var text = new JString(Files.readAllBytes(Paths.get(source)), "UTF-8");
if (text.length() > 0 && text.charAt(0) == 0xFEFF) text = text.substring(1);
var data = JSON.parse(text);

// Export.json list -> SDE table (DAL ExportImportModel; Sde.Core.Exchange.SdeDataset).
var tables = {
	Properties: "Property", Assessments: "Assessment", Attachments: "Attachment", ElementPercentages: "ElementPercentage",
	CostAdjustments: "CostAdjustmentResult", AdditionalAdjustments: "AdditionalAdjustment",
	CustomFieldsResults: "CustomField_Results", Shapes: "Shapes"
};

var db = DatabaseBuilder.open(new File(target));
db.setDateTimeType(DateTimeType.LOCAL_DATE_TIME);
db.setAllowAutoNumberInsert(true);
// FEMA's schema gives several columns a DefaultValue of 0. Jet keeps an explicit NULL (FEMA's DAL inserted every
// column), but Jackcess would substitute the default; without expression evaluation it writes the NULL.
db.setEvaluateExpressions(false);

function convert(col, v) {
	if (v === null || v === undefined) return null;
	switch (String(col.getType())) {
		case "GUID": return "{" + String(v).toUpperCase() + "}";
		case "BOOLEAN": return v === true;
		case "SHORT_DATE_TIME": return LocalDateTime.parse(String(v));
		case "MONEY": case "NUMERIC": return new BigDecimal(String(v));
		case "DOUBLE": return JDouble.valueOf(String(v));
		case "FLOAT": return JFloat.valueOf(String(v));
		case "LONG": return JInteger.valueOf(String(v));
		case "INT": return JShort.valueOf(String(v));
		case "BYTE": return JByte.valueOf(String(v));
		default: return String(v);
	}
}

for (var list in tables) {
	var rows = data[list] || [];
	var table = db.getTable(tables[list]);
	var cols = {};
	table.getColumns().forEach(function (c) { cols[String(c.getName()).toLowerCase()] = c; });
	rows.forEach(function (r) {
		var row = new HashMap();
		for (var key in r) {
			var col = cols[key.toLowerCase()];
			if (!col) { print("  " + tables[list] + ": no column for " + key); continue; }
			row.put(col.getName(), convert(col, r[key]));
		}
		table.addRowFromMap(row);
	});
	print(tables[list] + ": " + rows.length + " rows");
}
// FEMA's empty database names the person who created it in its document properties; the fixture shouldn't.
[db.getSummaryProperties(), db.getUserDefinedProperties()].forEach(function (props) {
	if (props.get("Author") != null) { props.get("Author").setValue(""); props.save(); }
});
db.close();
