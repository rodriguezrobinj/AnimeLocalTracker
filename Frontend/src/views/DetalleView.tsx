import React, { useState, useEffect } from 'react';
import { Play, ArrowLeft, Cloud, Star, CheckCircle } from 'lucide-react';

interface DetalleViewProps {
  anime: any;
  onBack: () => void;
}

export function DetalleView({ anime, onBack }: DetalleViewProps) {
  const [episodios, setEpisodios] = useState<any[]>([]);

  useEffect(() => {
    // Pedir episodios a C#
    const handleMessage = (event: MessageEvent) => {
      try {
        const payload = JSON.parse(event.data);
        if (payload.type === 'UpdateEpisodios' && payload.aniListId === anime.aniListId) {
          setEpisodios(payload.data);
        }
      } catch (e) {
        // Ignorar
      }
    };

    const webViewObj = (window as any).chrome?.webview;
    if (webViewObj) {
      webViewObj.addEventListener('message', handleMessage);
      webViewObj.postMessage(JSON.stringify({ action: 'GetEpisodios', aniListId: anime.aniListId }));
    }

    return () => {
      if (webViewObj) {
        webViewObj.removeEventListener('message', handleMessage);
      }
    };
  }, [anime.aniListId]);

  const handlePlay = (ruta: string) => {
    const webViewObj = (window as any).chrome?.webview;
    if (webViewObj) {
      webViewObj.postMessage(JSON.stringify({ action: 'AbrirVideo', ruta }));
    }
  };

  const statusColor = anime.estado === 'RELEASING' ? 'text-green-400' : 'text-gray-400';

  return (
    <div className="flex flex-col w-full h-full p-8 overflow-y-auto">
      {/* Botón Volver */}
      <button 
        onClick={onBack}
        className="flex items-center space-x-2 text-textMuted hover:text-white transition-colors self-start mb-6"
      >
        <ArrowLeft size={20} />
        <span className="font-medium">Volver a la Galería</span>
      </button>

      <div className="flex flex-col lg:flex-row gap-10">
        {/* Columna Izquierda: Portada */}
        <div className="flex-shrink-0 w-full lg:w-80 flex flex-col space-y-4">
          <div className="relative w-full aspect-[2/3] rounded-2xl overflow-hidden shadow-2xl shadow-primary/10">
            <img src={anime.urlPortada} alt={anime.titulo} className="w-full h-full object-cover" />
            <div className={`absolute top-4 left-4 px-3 py-1 rounded-md text-xs font-bold text-white shadow-lg ${anime.estado === 'RELEASING' ? 'bg-green-500' : 'bg-gray-600/90'}`}>
              {anime.estado === 'RELEASING' ? 'En Emisión' : 'Finalizado'}
            </div>
          </div>
          
          <button className="w-full bg-primary hover:bg-primary/80 text-white py-3 rounded-lg font-bold flex justify-center items-center space-x-2 transition-colors">
            <Play size={20} className="fill-current" />
            <span>Ver Siguiente Episodio</span>
          </button>
        </div>

        {/* Columna Derecha: Info y Episodios */}
        <div className="flex-grow flex flex-col">
          <h1 className="text-5xl font-extrabold text-white mb-2 tracking-tight">{anime.titulo}</h1>
          <div className="flex items-center space-x-4 mb-6">
            <span className={`font-semibold ${statusColor}`}>
              {anime.estado === 'RELEASING' ? 'En Emisión' : 'Finalizado'}
            </span>
            <span className="text-textMuted">•</span>
            <span className="text-textMuted">{anime.episodiosVistos} de {anime.totalEpisodios || '?'} episodios vistos</span>
          </div>

          <div className="flex flex-wrap gap-2 mb-6">
            {(anime.generos?.split(',') || []).map((g: string) => g.trim()).map((g: string) => (
              <span key={g} className="px-3 py-1 bg-surfaceLight text-textMain rounded-full text-xs font-semibold">
                {g}
              </span>
            ))}
          </div>

          <p className="text-textMuted leading-relaxed mb-10 max-w-4xl" dangerouslySetInnerHTML={{ __html: anime.sinopsis || 'Sin sinopsis disponible.' }} />

          {/* Lista de Episodios */}
          <h2 className="text-2xl font-bold text-white mb-4">Episodios Guardados</h2>
          
          <div className="flex flex-col space-y-2">
            {episodios.length === 0 ? (
              <p className="text-textMuted italic">Cargando episodios locales...</p>
            ) : (
              episodios.map((ep) => (
                <div 
                  key={ep.id} 
                  className={`flex items-center justify-between p-4 rounded-xl transition-all duration-300 ${ep.vistoLocal ? 'bg-surfaceLight/50' : 'bg-surfaceLight hover:bg-surfaceLight/80'}`}
                >
                  <div className="flex items-center space-x-4">
                    <button 
                      onClick={() => handlePlay(ep.rutaArchivo)}
                      className="w-10 h-10 rounded-full bg-primary/20 hover:bg-primary text-primary hover:text-white flex items-center justify-center transition-all group"
                    >
                      <Play size={18} className="ml-1 group-hover:scale-110 transition-transform" />
                    </button>
                    <div>
                      <h4 className={`font-bold ${ep.vistoLocal ? 'text-textMuted' : 'text-white'}`}>Episodio {ep.numero}</h4>
                      <p className="text-xs text-textMuted mt-1">Descargado localmente</p>
                    </div>
                  </div>

                  <div className="flex items-center space-x-3 text-textMuted">
                    {ep.estaSubidoNube && <Cloud size={18} className="text-blue-400" title="Subido a la Nube" />}
                    {ep.vistoLocal && <CheckCircle size={18} className="text-green-400" title="Visto" />}
                    {!ep.vistoLocal && <Star size={18} className="text-yellow-400" title="Nuevo" />}
                  </div>
                </div>
              ))
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
