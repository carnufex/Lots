export type Passage = { id: string; title: string; covers: string; text: string }

/**
 * Texts to read when recording your own voice. Each is written to give the voice model something different to learn from:
 * the sounds of the language, questions and exclamations, numbers, a calm and a lively delivery. Around 15-25 seconds read
 * at a normal pace. The first one is the best single choice; reading two different ones gives the most range.
 */
export const PASSAGES: Record<'sv' | 'en', Passage[]> = {
  sv: [
    {
      id: 'sv-balanced',
      title: 'Allsidig (rekommenderas)',
      covers: 'sj- och tj-ljud, långa och korta vokaler, å ä ö y, siffror, fråga och utrop',
      text: 'Hej, det här är min röst. Jag tycker om att gå långa promenader i skogen när det är lugnt och tyst. Vad gör du helst en ledig söndag? Igår köpte jag tjugotre körsbär, sju kex och en sjuk liten sked. Kul, va? Nu ska jag bara kolla att allt fungerar som det ska, sedan tar vi en kopp kaffe.',
    },
    {
      id: 'sv-calm',
      title: 'Lugn berättarröst',
      covers: 'jämn rytm, rd/rn/rs-ljud, långa meningar, mjuk intonation',
      text: 'Det var en tidig morgon i oktober. Dimman låg tung över sjön, och bakom husen steg solen långsamt över åkrarna. Jag stod en stund vid fönstret och lyssnade på tystnaden, innan jag hällde upp te och satte mig vid det gamla köksbordet.',
    },
    {
      id: 'sv-lively',
      title: 'Livlig och glad',
      covers: 'känslor, höjd och sänkt ton, frågor, utrop, tempoväxlingar',
      text: 'Nej, men vad roligt att du ringer! Jag har just fått fantastiska nyheter: vi vann! Tänk dig, första plats, av över hundra lag! Jag kan knappt tro det. Berätta, berätta allt, vad hände sedan? Hur kändes det? Vi måste fira i helgen, det här är verkligen värt en tårta!',
    },
    {
      id: 'sv-tech',
      title: 'Vardagligt och tekniskt',
      covers: 'det du faktiskt säger till agenten: tjänster, siffror, lugnt och sakligt',
      text: 'Servern startade om klockan fyra på natten, men alla tjänster kom upp utan problem. Disken är nästan full, så jag rensar loggarna och flyttar gamla säkerhetskopior. Om det händer igen vill jag få ett larm direkt, helst innan användarna märker något.',
    },
  ],
  en: [
    {
      id: 'en-balanced',
      title: 'Balanced (recommended)',
      covers: 'th, w/v, ch/sh sounds, long and short vowels, numbers, a question and an exclamation',
      text: 'Hello, this is my voice. I enjoy long walks in the woods when everything is calm and quiet. What do you usually do on a free Sunday? Yesterday I bought thirteen cherries, three chocolate bars and a very small wooden spoon. Funny, right? Now let me just check that everything works, and then we can have a cup of coffee.',
    },
    {
      id: 'en-calm',
      title: 'Calm storyteller',
      covers: 'steady rhythm, long sentences, soft intonation',
      text: 'It was an early morning in October. A thick mist lay over the lake, and behind the houses the sun rose slowly above the fields. I stood by the window for a while, listening to the silence, before I poured some tea and sat down at the old kitchen table.',
    },
    {
      id: 'en-lively',
      title: 'Lively and cheerful',
      covers: 'emotion, rising and falling pitch, questions, exclamations, changes of pace',
      text: "Oh wow, I'm so glad you called! I've just had the most amazing news: we won! Can you believe it, first place, out of more than a hundred teams? Tell me everything, what happened next? How did it feel? We have to celebrate this weekend, this definitely deserves a cake!",
    },
    {
      id: 'en-tech',
      title: 'Everyday and technical',
      covers: 'what you will actually say to the agent: services, numbers, calm and matter-of-fact',
      text: "The server restarted at four in the morning, but every service came back up without trouble. The disk is almost full, so I'm cleaning the logs and moving the old backups. If it happens again, I want an alert straight away, preferably before the users notice anything.",
    },
  ],
}
