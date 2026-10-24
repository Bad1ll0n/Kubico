import 'dart:async';

/// Junta numa só as recargas pedidas em rajada pelos eventos do tempo real (P2): a recarga corre [delay] depois do
/// último pedido (no máximo [maxWait] depois do primeiro, para uma rajada sem fim não a adiar para sempre). Nunca há
/// duas ao mesmo tempo: um pedido que chegue durante uma recarga faz mais uma no fim.
///
/// Ex.: o Pai marca 30 artigos e limpa o carrinho; a app da Mãe faz um pedido em vez de 60.
class CoalescedReload {
  CoalescedReload(
    this._reload, {
    this.delay = const Duration(milliseconds: 300),
    this.maxWait = const Duration(seconds: 2),
  });

  final Future<void> Function() _reload;
  final Duration delay;
  final Duration maxWait;

  Timer? _timer;
  DateTime? _firstRequest;
  bool _running = false;
  bool _again = false;
  bool _disposed = false;

  /// Há uma recarga marcada ou a decorrer.
  bool get isPending => _timer != null || _running;

  /// Pede uma recarga (junta-se às que estiverem marcadas).
  void request() {
    if (_disposed) return;
    if (_running) {
      _again = true;
      return;
    }
    final now = DateTime.now();
    _firstRequest ??= now;
    final waited = now.difference(_firstRequest!);
    _timer?.cancel();
    _timer = Timer(waited >= maxWait ? Duration.zero : delay, _run);
  }

  Future<void> _run() async {
    _timer = null;
    _firstRequest = null;
    if (_disposed) return;
    _running = true;
    try {
      await _reload();
    } catch (_) {
      // Quem recarrega trata dos seus erros; aqui só não se deixa a fila presa.
    } finally {
      _running = false;
    }
    if (_again && !_disposed) {
      _again = false;
      request();
    }
  }

  /// Cancela o que estiver marcado (ao fechar o ecrã).
  void dispose() {
    _disposed = true;
    _timer?.cancel();
    _timer = null;
  }
}
