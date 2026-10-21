# Phone form

The phone form lets inspectors work from a phone in the field while SDE runs on a Mac nearby (in a vehicle, a field
office, a shelter). The Mac serves a small web page over its Wi-Fi network; a phone on the same network opens it in its
browser (Safari on iPhone, Chrome on Android), with nothing to install. From the phone an inspector can:

- see the structures assigned to their team, in route order (**My assignments**);
- search for a structure by address, owner, parcel number, record ID or the code on a field worksheet, or add a new
  one (residential or non-residential);
- fill in the fields of its assessment, step by step, as in the Mac's editor (only the fields that apply are shown, and
  the same rules run: a new structure type clears the stories, a cause other than flood clears the flood details, and so
  on);
- take photos with the phone's camera, or choose them from the phone, and add them to the assessment.

Every change is saved on the Mac straight away. If the same assessment is open in the Mac's editor, it updates. Edit
history shows each change with **Phone** as the way it was made and the name the inspector typed when pairing as the
person.

The phone form doesn't do everything the Mac does: damage percentages by element, cost adjustments, statuses, reports,
deleting, and import and export stay on the Mac.

## Using it

### On the Mac

1. Open **Settings** and find **Phone form**.
2. Press **Start phone form**. SDE shows a QR code, the address phones use (for example `http://192.168.1.20:52814/`) and
   a 6-digit PIN.
3. The first time, macOS may ask whether SDE may accept incoming network connections, or find devices on your local
   network. Allow it; otherwise phones can't reach the Mac.

While the phone form runs, SDE keeps the Mac from going to sleep. It stops when you press **Stop phone form**, when you
quit SDE, and by itself after 12 hours (the card shows how long is left).

### On a phone

1. Join the same Wi-Fi network as the Mac.
2. Scan the QR code with the camera and open the link, or type the address into the browser and use the PIN.
3. Type your name and tap **Pair**. The name goes into the Mac's edit history next to each change you make.
4. If the office has made teams, choose yours under **My assignments**; the phone remembers it. Tap a structure on the
   list, search for one (the code printed on a field worksheet works too), or tap **Add a structure**. Open a step and fill in the fields; each one is saved when you
   leave it ("Saved on the Mac"). Use **Add photo** to take or choose a photo.

Several phones can be paired at once. The QR code and PIN work for one phone, for 10 minutes; a new code appears as soon
as one is used. **New code** on the Mac makes a fresh one at any time. Each paired phone is listed on the Mac with
**Revoke**, which signs it out immediately.

An assessment that is **Final** or **Letter sent** can be read on the phone but not changed; the phone says so, and it
can be reopened on the Mac.

Photos can be JPEG, PNG or HEIC, up to 25 MB each. HEIC photos (the iPhone's default format) are converted to JPEG on the
Mac, as when photos are added on the Mac.

## Security

**Traffic is not encrypted.** The phone form uses plain HTTP, so anything sent between the phone and the Mac (owner
names and addresses, field values, photos, and the phone's session cookie) can be read by anyone who can watch traffic
on that network. Use it only on a network you trust: your organization's Wi-Fi, or a hotspot you control with a strong
password. Don't use it on open or shared public Wi-Fi.

Plain HTTP was chosen because a Mac on a field network has no certificate a phone would trust; a self-signed one would
make every phone show a security warning that inspectors would learn to click through.

What protects the phone form:

- **Off by default.** Nothing listens until someone presses Start on the Mac. It stops after 12 hours, when SDE quits, and
  when someone presses Stop. Stopping ends every phone's session.
- **Local network only.** The server listens only on the Mac's private network address (10.x.x.x, 172.16-31.x.x or
  192.168.x.x), never on every address, so it isn't reachable on a public address the Mac might have. It also refuses any
  request whose sender isn't on a private network or the Mac itself, and any request addressed to another host name (so
  a web page elsewhere can't reach it through a look-alike domain name).
- **Pairing.** Until a phone has paired, the server shows only the pairing screen. Pairing needs the one-time token in
  the QR code or the 6-digit PIN on the Mac's screen, so it needs someone who can see the Mac. Both work once and expire
  after 10 minutes. After 5 wrong PINs, PIN pairing pauses for 10 minutes (the QR code still works).
- **Sessions.** A paired phone gets a random 256-bit session cookie that page scripts can't read (HttpOnly), that the
  browser sends only to this server and only from its own pages (SameSite=Strict), and that ends when the server stops.
  The Mac keeps only a hash of it. The Mac lists every paired phone and can revoke any of them.
- **Requests from other sites are refused.** Every request that changes something must carry a custom header
  (`X-SDE: 1`) that a page on another site can't add without the browser asking the server first, which it never
  allows. The page itself is served with a strict Content Security Policy and builds everything from text, never from
  HTML in the data.
- **Limits.** Requests are limited to 1 MB, photos to 25 MB, and uploaded files must really be JPEG, PNG or HEIC images
  (checked by their content, not their name).
- **Same rules as the Mac.** Values are checked like the editor checks them, Final and Letter sent assessments can't be
  changed, and every change is recorded in edit history.

What it doesn't protect against:

- Someone who can watch traffic on the network can read what is sent, and could copy a phone's session cookie and act as
  that phone until it is revoked or the server stops.
- Anyone on the same network can open the pairing page. Without the QR token or PIN they can't get further, but they
  could use up the PIN attempts and pause PIN pairing for 10 minutes (the QR code still works).
- A paired phone can change any assessment that isn't Final or Letter sent, and add structures and photos. Pair only
  phones you trust, and revoke a phone that is lost.
- It checks devices by network address and pairing, not by user account. There are no passwords.

### Networks where it won't work

Guest networks and some office networks keep devices from reaching one another ("client isolation"); phones on those
can't reach the Mac. Use a network without it, or a hotspot from a phone or a travel router. If the Mac has no private
network address (for example, it is connected only by a cellular modem with a public address), the phone form won't
start.

## For developers

- `src/Sde.Field`: the server (`FieldServer`, Kestrel minimal APIs), pairing and sessions (`PairingManager`), network
  checks (`NetworkAccess`), field reading and checking (`FieldApi`, built on `FieldCatalog` and `FieldRules`), and the
  page (`wwwroot/`, plain HTML, CSS and JavaScript, embedded). It references only Sde.Core and Sde.Data; the app supplies
  an `IFieldWriter` (its `AssessmentWriteCoordinator`, so changes reach the open editor) and an `IPhotoConverter`
  (`PhotoConversion`, which uses macOS's `sips`).
- `src/Sde.App/Services/PhoneFormService.cs` and `Views/PhoneFormPanel.axaml`: the Settings card, `caffeinate` while
  running, and stopping on quit.
- `tests/Sde.Field.Tests`: the server over real HTTP on 127.0.0.1 (pairing, cookies, the custom header, locks, uploads,
  history). `tests/Sde.App.Tests/PhoneFormUiTests.cs`: the Settings card and a change reaching the open editor.
- The packaged app's `Info.plist` has `NSLocalNetworkUsageDescription` (see `tools/packaging/package_app.sh`).
