{{flutter_js}}
{{flutter_build_config}}

// O indicador de carregamento do index.html sai quando a app desenha o primeiro ecrã (P8).
window.addEventListener('flutter-first-frame', () => document.getElementById('kubico-loading')?.remove(), { once: true });

// O motor gráfico (CanvasKit ou skwasm) vem da própria app, não do gstatic.com da Google (F4-18): sem pedidos a
// terceiros antes de a pessoa entrar, e funciona com a CSP apertada.
_flutter.loader.load({
  config: {
    canvasKitBaseUrl: 'canvaskit/',
  },
  serviceWorkerSettings: {
    serviceWorkerVersion: {{flutter_service_worker_version}},
  },
});
