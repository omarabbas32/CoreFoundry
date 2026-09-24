import { redirect } from "next/navigation";

// The dashboard starts at the projects list; the (app) layout sends signed-out visitors to /login.
export default function Home() {
  redirect("/projects");
}
