// Feedback for the WorkflowHub upload form. The form works without this script.
// The button starts the login on WorkflowHub, so it shows a busy text until the browser leaves
// this page. The script makes no request of its own.
// An item that an earlier commit already put on WorkflowHub offers a new version or a new entry.
// The script hides the visibility of such an item while the new version is chosen, and it marks the
// team choice as required only while a new entry is selected, because a new version needs no team.
(function () {
  "use strict";

  var form = document.querySelector("form[data-wfh-upload]");
  if (!form) return;

  var items = Array.prototype.slice.call(
    form.querySelectorAll('input[type="checkbox"][name="item"]'));
  // Items with data-wfh-caution stay selectable one by one. "Select all" and its indeterminate
  // state leave them alone.
  var bulk = items.filter(function (item) { return !item.hasAttribute("data-wfh-caution"); });
  var selectAll = form.querySelector("[data-wfh-select-all]");
  var button = form.querySelector("[data-wfh-submit]");
  var message = form.querySelector("[data-wfh-empty]");
  var status = form.querySelector("[data-wfh-status]");
  var idleText = button ? button.textContent : "";
  var busyText = "Opening WorkflowHub\u2026";
  var busy = false;

  function checkedCount(list) {
    return list.filter(function (item) { return item.checked; }).length;
  }

  function hideMessage() {
    if (message) message.hidden = true;
  }

  // Items with an earlier entry carry a choice. Items without one always become a new entry.
  var choices = Array.prototype.slice.call(form.querySelectorAll("[data-wfh-mode]"));
  var teamRadios = Array.prototype.slice.call(
    form.querySelectorAll('input[type="radio"][name="team"]'));

  function choiceOf(item) {
    var row = item.closest("li");
    return row ? row.querySelector("[data-wfh-mode]") : null;
  }

  function newVersionChosen(choice) {
    var picked = choice ? choice.querySelector('input[type="radio"]:checked') : null;
    return !!picked && picked.value === "version";
  }

  // The visibility applies to a new entry only. The hint is for readers without this script.
  function syncVisibility(choice) {
    var row = choice.closest("li");
    var visibility = row ? row.querySelector("[data-wfh-visibility]") : null;
    if (!visibility) return;
    visibility.hidden = newVersionChosen(choice);
    var hint = visibility.querySelector("[data-wfh-visibility-hint]");
    if (hint) hint.hidden = true;
  }

  // The team radio group comes without "required" when the page has a choice. A selection that
  // only adds versions needs no team, and any other selection needs one.
  function syncTeamRequired() {
    if (choices.length === 0 || teamRadios.length === 0) return;
    var ticked = items.filter(function (item) { return item.checked; });
    var versionsOnly = ticked.length > 0 && ticked.every(function (item) {
      return newVersionChosen(choiceOf(item));
    });
    teamRadios.forEach(function (radio) { radio.required = !versionsOnly; });
  }

  function syncChoices() {
    choices.forEach(syncVisibility);
    syncTeamRequired();
  }

  function syncSelectAll() {
    if (!selectAll) return;
    var count = checkedCount(bulk);
    selectAll.checked = bulk.length > 0 && count === bulk.length;
    selectAll.indeterminate = count > 0 && count < bulk.length;
  }

  function refuseEmptySelection(event) {
    if (checkedCount(items) > 0) return false;
    event.preventDefault();
    if (message) message.hidden = false;
    if (items.length > 0) items[0].focus();
    return true;
  }

  function reset() {
    busy = false;
    if (!button) return;
    button.removeAttribute("aria-busy");
    button.disabled = false;
    button.textContent = idleText;
    if (status) status.textContent = "";
  }

  if (selectAll) {
    var label = selectAll.closest("label");
    if (label && bulk.length > 0) label.hidden = false;
    selectAll.addEventListener("change", function () {
      bulk.forEach(function (item) { item.checked = selectAll.checked; });
      syncTeamRequired();
      hideMessage();
    });
  }

  items.forEach(function (item) {
    item.addEventListener("change", function () {
      syncSelectAll();
      syncTeamRequired();
      hideMessage();
    });
  });
  choices.forEach(function (choice) {
    choice.addEventListener("change", function () {
      syncVisibility(choice);
      syncTeamRequired();
    });
  });
  syncSelectAll();
  syncChoices();

  if (!button) return;

  // The click comes before the browser's own validation, so an empty selection gets its message first.
  button.addEventListener("click", function (event) {
    if (busy) {
      event.preventDefault();
      return;
    }
    refuseEmptySelection(event);
  });

  // The submit event only fires for a valid form. The button is disabled after this event, so the form still posts.
  form.addEventListener("submit", function (event) {
    if (busy) {
      event.preventDefault();
      return;
    }
    if (refuseEmptySelection(event)) return;

    busy = true;
    button.setAttribute("aria-busy", "true");
    button.textContent = busyText;
    if (status) status.textContent = busyText;
    window.setTimeout(function () { button.disabled = true; }, 0);
  });

  // A page restored from the back-forward cache keeps the busy state, and so can a page that
  // the browser reloads with its form state. Every show of the page starts clean.
  window.addEventListener("pageshow", reset);
  window.addEventListener("pageshow", syncChoices);
})();
