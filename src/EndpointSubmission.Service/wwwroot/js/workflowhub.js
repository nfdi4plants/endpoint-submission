// Feedback for the WorkflowHub upload form. The form works without this script.
// The button starts the login on WorkflowHub, so it shows a busy text until the browser leaves
// this page. The script makes no request of its own.
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
      hideMessage();
    });
  }

  items.forEach(function (item) {
    item.addEventListener("change", function () {
      syncSelectAll();
      hideMessage();
    });
  });
  syncSelectAll();

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
})();
