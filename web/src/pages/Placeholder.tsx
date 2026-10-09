export default function Placeholder({ title, text }: { title: string; text: string }) {
  return (
    <section>
      <h1>{title}</h1>
      <div className="empty">
        <p className="muted">{text}</p>
      </div>
    </section>
  )
}
