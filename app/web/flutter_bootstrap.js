{{flutter_js}}
{{flutter_build_config}}

// O indicador de carregamento do index.html sai quando a app desenha o primeiro ecrã (P8).
window.addEventListener('flutter-first-frame', () => document.getElementById('kubico-loading')?.remove(), { once: true });

_flutter.loader.load({
  serviceWorkerSettings: {
    serviceWorkerVersion: {{flutter_service_worker_version}},
  },
});
