import React, { useState, useEffect } from 'react';
import { Calendar as CalendarIcon, Clock } from 'lucide-react';

export function CalendarView() {
  const [schedule, setSchedule] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const handleMessage = (event: MessageEvent) => {
      try {
        const payload = JSON.parse(event.data);
        if (payload.type === 'UpdateCalendario') {
          setSchedule(payload.data);
          setLoading(false);
        }
      } catch (e) {
        // Ignorar
      }
    };

    const webViewObj = (window as any).chrome?.webview;
    if (webViewObj) {
      webViewObj.addEventListener('message', handleMessage);
      webViewObj.postMessage(JSON.stringify({ action: 'GetCalendario' }));
    } else {
      // Dummy data for testing if not in webview
      setTimeout(() => {
        setSchedule([
          { aniListId: 1, titulo: 'One Piece', urlPortada: 'https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx21-YCDoj1EkAxFn.jpg', numeroEpisodio: 1100, diaSemana: 0, horaEmisionFormateada: '09:30' },
          { aniListId: 2, titulo: 'Jujutsu Kaisen', urlPortada: 'https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx113415-bbBWj4pEFseh.jpg', numeroEpisodio: 47, diaSemana: 4, horaEmisionFormateada: '23:56' }
        ]);
        setLoading(false);
      }, 1000);
    }

    return () => {
      if (webViewObj) {
        webViewObj.removeEventListener('message', handleMessage);
      }
    };
  }, []);

  const days = [
    { id: 1, name: 'Lunes' },
    { id: 2, name: 'Martes' },
    { id: 3, name: 'Miércoles' },
    { id: 4, name: 'Jueves' },
    { id: 5, name: 'Viernes' },
    { id: 6, name: 'Sábado' },
    { id: 0, name: 'Domingo' }
  ];

  return (
    <div className="p-8 w-full h-full flex flex-col overflow-y-auto">
      <div className="flex items-center space-x-3 mb-8">
        <div className="p-3 bg-primary/20 rounded-lg text-primary">
          <CalendarIcon size={28} />
        </div>
        <h1 className="text-4xl font-extrabold text-white tracking-tight">Calendario de Emisión</h1>
      </div>

      {loading ? (
        <div className="flex-1 flex flex-col items-center justify-center text-textMuted">
          <div className="w-12 h-12 border-4 border-primary border-t-transparent rounded-full animate-spin mb-4"></div>
          <p>Consultando servidores de AniList...</p>
        </div>
      ) : (
        <div className="flex gap-4 overflow-x-auto pb-4 h-full">
          {days.map(day => {
            const dayAnimes = schedule.filter(a => a.diaSemana === day.id).sort((a, b) => a.horaEmisionFormateada.localeCompare(b.horaEmisionFormateada));
            
            return (
              <div key={day.id} className="flex-shrink-0 w-72 flex flex-col bg-surfaceLight/30 rounded-xl overflow-hidden border border-white/5">
                <div className="bg-surface p-4 text-center border-b border-white/5 shadow-md">
                  <h3 className="font-bold text-lg text-white">{day.name}</h3>
                  <span className="text-xs font-medium text-primary bg-primary/10 px-2 py-0.5 rounded-full">{dayAnimes.length} estrenos</span>
                </div>
                
                <div className="p-4 flex-1 overflow-y-auto flex flex-col space-y-4">
                  {dayAnimes.length === 0 ? (
                    <div className="text-center text-textMuted text-sm py-10 opacity-50">
                      Ningún anime en emisión este día.
                    </div>
                  ) : (
                    dayAnimes.map((anime, idx) => (
                      <div key={`${anime.aniListId}-${idx}`} className="bg-surface rounded-lg overflow-hidden border border-white/10 hover:border-primary/50 transition-colors shadow-lg group">
                        <div className="relative h-32 w-full">
                          <img src={anime.urlPortada} alt={anime.titulo} className="absolute inset-0 w-full h-full object-cover group-hover:scale-105 transition-transform duration-500" />
                          <div className="absolute inset-0 bg-gradient-to-t from-black/90 to-transparent"></div>
                          
                          <div className="absolute bottom-2 left-2 right-2">
                            <h4 className="text-white font-bold text-sm leading-tight truncate">{anime.titulo}</h4>
                          </div>
                          
                          <div className="absolute top-2 right-2 bg-black/80 backdrop-blur-md px-2 py-1 rounded text-xs font-bold text-white flex items-center space-x-1 border border-white/10">
                            <Clock size={12} className="text-primary" />
                            <span>{anime.horaEmisionFormateada}</span>
                          </div>
                        </div>
                        
                        <div className="p-2 bg-surfaceLight flex justify-between items-center text-xs">
                          <span className="text-textMuted font-medium">Episodio</span>
                          <span className="text-white font-bold bg-white/10 px-2 py-0.5 rounded">
                            {anime.numeroEpisodio}
                          </span>
                        </div>
                      </div>
                    ))
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
