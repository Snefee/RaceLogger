# Google Sheets setup

RaceLogger stores your laps in your own Google Sheet, through a small Google Apps Script that acts as a web
endpoint. Setup takes about 5 minutes and needs no coding. Menu names may differ slightly depending on your
Google account language.

## 1. Create the spreadsheet

1. Go to [sheets.google.com](https://sheets.google.com) and create a blank spreadsheet (e.g. "RaceLogger").
2. Rename the first tab to exactly `Raw_Data` (double-click the tab name). The name is case-sensitive.
3. In row 1, add these headers. RaceLogger skips row 1 and reads columns **by position**, so keep the order:

   | A | B | C | D | E | F | G | H | I | J | K |
   |---|---|---|---|---|---|---|---|---|---|---|
   | Track | Driver | Class | Car | Lap Time | Sector 1 | Sector 2 | Sector 3 | Session | Game | Date |

4. Select columns **E to H**, then choose **Format → Number → Plain text**. Without this, Google Sheets may
   turn lap times like `02:22.802` into time values and the leaderboard will not be able to read them.

## 2. Add the script

1. In the spreadsheet, open **Extensions → Apps Script**.
   > **Seeing "Couldn't open the file"?** This is a known Google Sheets bug that happens when you are signed in
   > to more than one Google account in the same browser. Open an incognito window, sign in to only the
   > account that owns the spreadsheet, and continue from there.   
2. Delete the default code and paste this:

```javascript
function doPost(e) {
  // Parse incoming JSON data from the telemetry app
  var data = JSON.parse(e.postData.contents);

  var sheet = SpreadsheetApp.getActiveSpreadsheet().getSheetByName("Raw_Data");

  // Append the new lap record to the bottom of the raw data sheet
  sheet.appendRow([
    data.track,
    data.driver,
    data.carClass,
    data.carModel,
    data.lapTime,
    data.sector1,
    data.sector2,
    data.sector3,
    data.session,
    data.game,
    data.date
  ]);

  // Return success response to the app
  return ContentService.createTextOutput("Success");
}

function doGet(e) {
  // Get Raw_Data
  var sheet = SpreadsheetApp.getActiveSpreadsheet().getSheetByName("Raw_Data");

  if (!sheet) {
    return ContentService.createTextOutput(JSON.stringify({error: "Tab Raw_Data does not exist"}))
      .setMimeType(ContentService.MimeType.JSON);
  }

  var data = sheet.getDataRange().getValues();

  return ContentService.createTextOutput(JSON.stringify(data))
    .setMimeType(ContentService.MimeType.JSON);
}
```

3. Click **Save** and give the project a name.

## 3. Deploy it as a web app

1. Click **Deploy → New deployment**.
2. Click the gear icon next to "Select type" and choose **Web app**.
3. Set **Execute as: Me** and **Who has access: Anyone**.
4. Click **Deploy** and authorize the script when asked. Google will warn that the app is not verified
   (it is your own script): click **Advanced → Go to <project name> (unsafe) → Allow**.
5. Copy the **Web app URL** (it ends with `/exec`).

To check that it works, open the URL in your browser. You should see your header row as JSON.

## 4. Connect RaceLogger

Open RaceLogger → **Settings**, paste the Web app URL into **Google Sheets Link** and press **Save & Sync**.
Your next valid lap will appear in the `Raw_Data` tab.

## Changing the script later

After any edit, create a new version: **Deploy → Manage deployments → pencil icon → Version: New version →
Deploy**. The URL stays the same. If you skip this, the old code keeps running.

## Keep your URL private

With access set to "Anyone", whoever has the URL can read your laps and add rows. Do not post it publicly
or commit it to a repository. If it leaks, create a new deployment (**Deploy → New deployment**), put the new
URL into Settings and archive the old one in **Manage deployments**.

Data is stored only in your own Google account. The driver name comes from your Le Mans Ultimate profile.

## Troubleshooting

| Problem | Cause |
|---|---|
| Received HTML page instead of data | Script not deployed as a **new version**, or access is not set to **Anyone** |
| Script returned an error | The tab is not named exactly `Raw_Data` |
| The table in Google Sheets is empty | No laps recorded yet |
| Failed to send to Sheets (error code) | Wrong or outdated URL in Settings |
