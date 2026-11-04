/// Mostrar um PDF: na web, dentro da app com o visualizador do navegador; nas outras plataformas, no visualizador do
/// sistema (o PDFium compilado para cada plataforma pesava muito e obrigava a ter o compilador de C para os testes).
library;

export 'pdf_view_io.dart' if (dart.library.js_interop) 'pdf_view_web.dart';
