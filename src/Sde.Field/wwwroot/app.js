// SDE phone form: a small page served by SDE on the Mac. No libraries; everything is built with DOM calls (never
// innerHTML), so text from the database is always shown as text.
(function () {
  "use strict";

  var state = { assessment: null, saving: Promise.resolve(), pairToken: null };

  function $(id) { return document.getElementById(id); }

  function el(tag, attrs, children) {
    var e = document.createElement(tag);
    if (attrs) {
      Object.keys(attrs).forEach(function (k) {
        if (k === "text") { e.textContent = attrs[k]; }
        else if (k === "className") { e.className = attrs[k]; }
        else if (attrs[k] === true) { e.setAttribute(k, ""); }
        else if (attrs[k] !== false && attrs[k] != null) { e.setAttribute(k, attrs[k]); }
      });
    }
    (children || []).forEach(function (c) { if (c) { e.appendChild(c); } });
    return e;
  }

  function show(section) {
    ["pair", "find", "edit"].forEach(function (s) { $(s).hidden = s !== section; });
    window.scrollTo(0, 0);
  }

  function message(text) {
    var m = $("message");
    m.textContent = text || "";
    m.hidden = !text;
  }

  // Every call: same-origin cookie; changes also carry X-SDE: 1 (the server refuses them without it).
  function api(method, path, body, isForm) {
    var headers = { "Accept": "application/json" };
    if (method !== "GET") { headers["X-SDE"] = "1"; }
    if (body && !isForm) { headers["Content-Type"] = "application/json"; }
    return fetch(path, {
      method: method,
      headers: headers,
      credentials: "same-origin",
      body: body ? (isForm ? body : JSON.stringify(body)) : undefined
    }).then(function (res) {
      return res.json().catch(function () { return {}; }).then(function (data) {
        if (res.status === 401 && path !== "/api/pair") {
          showPair(null, data.error);
        }
        return { ok: res.ok, status: res.status, data: data };
      });
    }, function () {
      return { ok: false, status: 0, data: { error: "Can't reach the Mac. Check that this phone is on the same Wi-Fi and the phone form is still running." } };
    });
  }

  // ───────── Pairing ─────────

  function showPair(token, text) {
    if (token) { state.pairToken = token; }
    $("pin-row").hidden = !!state.pairToken;
    $("pair-help").textContent = state.pairToken
      ? "Type your name, then tap Pair."
      : "Type the 6-digit PIN shown on the Mac (Settings, Phone form), or scan its QR code with the camera.";
    $("who").hidden = true;
    message(text || "");
    show("pair");
  }

  $("pair-form").addEventListener("submit", function (e) {
    e.preventDefault();
    var name = $("pair-name").value.trim();
    if (!name) { message("Type your name first."); $("pair-name").focus(); return; }
    var body = { name: name };
    if (state.pairToken) { body.token = state.pairToken; } else { body.pin = $("pair-pin").value.trim(); }
    api("POST", "/api/pair", body).then(function (r) {
      if (!r.ok) {
        // A used or expired link: fall back to the PIN.
        state.pairToken = null;
        showPair(null, r.data.error || "Pairing didn't work. Try again.");
        return;
      }
      state.pairToken = null;
      $("pair-pin").value = "";
      paired(r.data.name);
    });
  });

  function paired(name) {
    $("who").textContent = "Paired as " + name;
    $("who").hidden = false;
    message("");
    show("find");
    teams();
    search("");
  }

  // ───────── My assignments ─────────

  var teamKey = "sde-team";

  function savedTeam() {
    try { return localStorage.getItem(teamKey) || ""; } catch (e) { return ""; }
  }

  function teams() {
    api("GET", "/api/teams").then(function (r) {
      if (!r.ok || r.data.length === 0) { $("mine").hidden = true; return; }
      var select = $("team");
      while (select.options.length > 1) { select.remove(1); }
      r.data.forEach(function (t) { select.appendChild(el("option", { value: t.teamId, text: t.name })); });
      var saved = savedTeam();
      select.value = r.data.some(function (t) { return t.teamId === saved; }) ? saved : "";
      $("mine").hidden = false;
      assignments(select.value);
    });
  }

  function assignments(teamId) {
    var list = $("assignments");
    list.textContent = "";
    if (!teamId) { return; }
    api("GET", "/api/teams/" + teamId + "/assignments").then(function (r) {
      if (!r.ok) { message(r.data.error); return; }
      if (r.data.length === 0) {
        list.appendChild(el("li", { text: "Nothing assigned to this team yet." }));
        return;
      }
      r.data.forEach(function (a) {
        var prefix = a.routeOrder ? a.routeOrder + ". " : "";
        var extra = [a.shortCode, a.status !== "Assigned" ? a.status : null, a.dueDate ? "Due " + a.dueDate : null];
        list.appendChild(structureItem(a.structure, prefix, extra));
      });
    });
  }

  $("team").addEventListener("change", function () {
    try { localStorage.setItem(teamKey, $("team").value); } catch (e) { }
    assignments($("team").value);
  });

  // ───────── Search and add ─────────

  function structureItem(s, prefix, extra) {
    var meta = [s.city, s.owner, s.parcel ? "Parcel " + s.parcel : null, s.recordId ? "Record " + s.recordId : null, s.result]
      .concat(extra || []).filter(function (x) { return x; }).join(" · ");
    var b = el("button", { type: "button" }, [
      el("span", { className: "addr", text: (prefix || "") + s.address }),
      el("span", { className: "meta", text: meta || s.structureType })
    ]);
    b.addEventListener("click", function () { openStructure(s); });
    return el("li", null, [b]);
  }

  function search(q) {
    api("GET", "/api/structures?q=" + encodeURIComponent(q)).then(function (r) {
      var list = $("results");
      list.textContent = "";
      if (!r.ok) { message(r.data.error); return; }
      if (r.data.length === 0) {
        list.appendChild(el("li", { text: q ? "Nothing matches. Add the structure below." : "No structures yet." }));
        return;
      }
      r.data.forEach(function (s) { list.appendChild(structureItem(s)); });
    });
  }

  $("search-form").addEventListener("submit", function (e) {
    e.preventDefault();
    search($("search").value.trim());
  });

  $("show-add").addEventListener("click", function () {
    $("add-form").hidden = false;
    $("show-add").hidden = true;
    $("add-number").focus();
  });

  $("cancel-add").addEventListener("click", function () {
    $("add-form").hidden = true;
    $("show-add").hidden = false;
  });

  $("add-form").addEventListener("submit", function (e) {
    e.preventDefault();
    var type = document.querySelector("input[name=type]:checked").value;
    var changes = [];
    Array.prototype.forEach.call($("add-form").querySelectorAll("input[data-key]"), function (i) {
      if (i.value.trim()) { changes.push({ key: i.getAttribute("data-key"), value: i.value.trim() }); }
    });
    api("POST", "/api/structures", { structureType: parseInt(type, 10), changes: changes }).then(function (r) {
      if (!r.ok) { message(r.data.error || "Couldn't add the structure."); return; }
      $("add-form").reset();
      $("add-form").hidden = true;
      $("show-add").hidden = false;
      renderAssessment(r.data, true);
    });
  });

  function openStructure(s) {
    message("");
    var call = s.assessmentId
      ? api("GET", "/api/assessments/" + s.assessmentId)
      : api("POST", "/api/properties/" + s.propertyId + "/assessments");
    call.then(function (r) {
      if (!r.ok) { message(r.data.error || "Couldn't open that structure."); return; }
      renderAssessment(r.data, true);
    });
  }

  $("back").addEventListener("click", function () {
    state.assessment = null;
    show("find");
    search($("search").value.trim());
  });

  // ───────── One assessment ─────────

  function fieldId(key) { return "f-" + key.replace(/\./g, "-"); }

  function keysOf(a) {
    return a.steps.map(function (s) { return s.fields.map(function (f) { return f.key + ":" + f.options.length; }).join(","); }).join("|");
  }

  function renderAssessment(a, fresh) {
    var previous = state.assessment;
    state.assessment = a;
    $("edit-heading").textContent = a.address;
    $("edit-summary").textContent = [a.structureType, a.status, a.result].filter(function (x) { return x; }).join(" · ");
    $("locked").hidden = !a.locked;
    $("locked").textContent = a.locked ? "This assessment is " + a.status + ", so it can't be changed here. It can be reopened on the Mac." : "";
    $("photo-count").textContent = a.photoCount === 1 ? "1 photo" : a.photoCount + " photos";
    Array.prototype.forEach.call($("photo-form").elements, function (c) { c.disabled = a.locked; });
    if (fresh) { $("save-status").textContent = ""; }

    if (!fresh && previous && keysOf(previous) === keysOf(a)) {
      // Same fields: only refresh values the person isn't typing in.
      a.steps.forEach(function (s) { s.fields.forEach(function (f) { setValue(f); }); });
    } else {
      var open = {};
      Array.prototype.forEach.call($("steps").querySelectorAll("details"), function (d) { open[d.getAttribute("data-step")] = d.open; });
      var steps = $("steps");
      steps.textContent = "";
      a.steps.forEach(function (s, i) {
        var d = el("details", { "data-step": String(s.step) }, [el("summary", { text: s.step + ". " + s.name })]);
        d.open = open[String(s.step)] !== undefined ? open[String(s.step)] : (fresh && i === 0);
        s.fields.forEach(function (f) { d.appendChild(control(f, a.locked)); });
        steps.appendChild(d);
      });
    }
    show("edit");
  }

  function control(f, locked) {
    var id = fieldId(f.key);
    var wrap = el("div", { className: "field" });
    var input;
    if (f.kind === "yesno") {
      input = el("input", { type: "checkbox", id: id });
      input.checked = f.value === "True";
      wrap.appendChild(el("label", { className: "check", "for": id }, [input, document.createTextNode(f.label)]));
    } else {
      var label = el("label", { "for": id, text: f.label });
      if (f.unit) { label.appendChild(el("span", { className: "unit", text: " (" + f.unit + ")" })); }
      wrap.appendChild(label);
      if (f.options.length > 0 || f.kind === "lookup" || f.kind === "state" || f.kind === "county" || f.kind === "structureType") {
        input = el("select", { id: id });
        if (f.kind !== "structureType") { input.appendChild(el("option", { value: "", text: "(none)" })); }
        f.options.forEach(function (o) { input.appendChild(el("option", { value: o.id, text: o.name })); });
      } else if (f.kind === "multiline") {
        input = el("textarea", { id: id, rows: "4" });
      } else if (f.kind === "date") {
        input = el("input", { id: id, type: "date" });
      } else if (f.kind === "number") {
        input = el("input", { id: id, type: "text", inputmode: "decimal", autocomplete: "off" });
      } else {
        input = el("input", { id: id, type: "text", autocomplete: "off" });
      }
      wrap.appendChild(input);
    }
    input.setAttribute("data-key", f.key);
    input.disabled = locked;
    var error = el("p", { className: "field-error", id: id + "-error", hidden: true });
    input.setAttribute("aria-describedby", error.id);
    wrap.appendChild(error);
    input.addEventListener("change", function () { save(f.key, readValue(input)); });
    setValue(f, input);
    return wrap;
  }

  function readValue(input) {
    if (input.type === "checkbox") { return input.checked ? "True" : "False"; }
    return input.value;
  }

  function setValue(f, input) {
    input = input || $(fieldId(f.key));
    if (!input || input === document.activeElement) { return; }
    var v = f.value == null ? "" : f.value;
    if (input.type === "checkbox") { input.checked = v === "True"; }
    else if (input.type === "date") { input.value = v.substring(0, 10); }
    else { input.value = v; }
    input.disabled = state.assessment.locked;
  }

  function fieldError(key, text) {
    var e = $(fieldId(key) + "-error");
    if (!e) { message(text); return; }
    e.textContent = text || "";
    e.hidden = !text;
  }

  // Saves run one after another, in the order they were made.
  function save(key, value) {
    var id = state.assessment.assessmentId;
    $("save-status").textContent = "Saving…";
    state.saving = state.saving.then(function () {
      return api("POST", "/api/assessments/" + id + "/changes", { changes: [{ key: key, value: value }] }).then(function (r) {
        if (!state.assessment || state.assessment.assessmentId !== id) { return; }
        if (!r.ok) {
          $("save-status").textContent = "";
          fieldError(key, r.data.error || "Not saved.");
          if (r.status === 409) { reload(id); }
          return;
        }
        fieldError(key, "");
        renderAssessment(r.data, false);
        $("save-status").textContent = "Saved on the Mac.";
      });
    });
  }

  function reload(id) {
    api("GET", "/api/assessments/" + id).then(function (r) { if (r.ok) { renderAssessment(r.data, false); } });
  }

  $("photo-form").addEventListener("submit", function (e) {
    e.preventDefault();
    var input = $("photo");
    if (!input.files || input.files.length === 0) { message("Take or choose a photo first."); return; }
    if (input.files[0].size > 25 * 1024 * 1024) { message("That photo is larger than 25 MB."); return; }
    var data = new FormData();
    data.append("photo", input.files[0]);
    data.append("description", $("photo-description").value);
    var id = state.assessment.assessmentId;
    var button = $("photo-form").querySelector("button");
    button.disabled = true;
    $("save-status").textContent = "Uploading photo…";
    api("POST", "/api/assessments/" + id + "/photos", data, true).then(function (r) {
      button.disabled = false;
      if (!r.ok) { $("save-status").textContent = ""; message(r.data.error || "The photo wasn't added."); return; }
      message("");
      $("photo-form").reset();
      state.assessment.photoCount = r.data.photoCount;
      $("photo-count").textContent = r.data.photoCount === 1 ? "1 photo" : r.data.photoCount + " photos";
      $("save-status").textContent = "Photo added on the Mac.";
    });
  });

  // ───────── Start ─────────

  var token = new URLSearchParams(window.location.search).get("t");
  if (token) {
    // Keep the one-time token out of the history and address bar.
    window.history.replaceState(null, "", "/");
  }
  api("GET", "/api/session").then(function (r) {
    if (r.ok) { paired(r.data.name); } else { showPair(token, token ? "" : null); }
  });
})();
