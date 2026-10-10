// Applies the saved theme before the first paint (#155), so a light-theme user never sees a dark flash. Kept tiny and
// synchronous; theme.ts takes over once the app has loaded. An external file because the CSP forbids inline scripts.
;(function () {
  var t = 'system'
  try {
    t = localStorage.getItem('lots.theme') || 'system'
  } catch (e) {}
  var light = t === 'light' || (t === 'system' && window.matchMedia && matchMedia('(prefers-color-scheme: light)').matches)
  document.documentElement.setAttribute('data-theme', light ? 'light' : 'dark')
  document.documentElement.style.colorScheme = light ? 'light' : 'dark'
})()
