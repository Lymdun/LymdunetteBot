namespace LymdunetteBot.Models;

public struct Meme {
    public string ImageUrl;

    Meme(string imageUrl) {
        this.ImageUrl = imageUrl;
    }

    public static Meme MondayMeme() {
        return new Meme("makise_monday.webm");
    }

    public static Meme TuesdayMeme() {
        return new Meme("everybody.webm");
    }

    public static Meme HachimiMichiMambo() {
        return new Meme("hachimi_michi_mambo.webm");
    }

    public static Meme Burnice() {
        return new Meme("burnice.mp4");
    }
}
