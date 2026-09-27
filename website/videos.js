/* Demo videos: silent loops that play while on screen.
   Browsers that block autoplay (Firefox can block even muted video) and visitors
   who ask for reduced motion get the poster and a play button instead. Every video
   gets a pause control, since moving content longer than five seconds must be stoppable. */
(function () {
  var reduceMotion = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var videos = document.querySelectorAll("video[data-demo]");
  if (!videos.length) return;

  function setup(video) {
    video.muted = true;
    video.removeAttribute("autoplay");

    var wrap = document.createElement("div");
    wrap.className = "demo";
    video.parentNode.insertBefore(wrap, video);
    wrap.appendChild(video);

    var button = document.createElement("button");
    button.type = "button";
    button.className = "demo__toggle";
    wrap.appendChild(button);

    // Set by the visitor's own pause; scrolling back into view then leaves it paused.
    var pausedByUser = reduceMotion;
    var visible = false;

    function render() {
      var playing = !video.paused;
      wrap.classList.toggle("is-paused", !playing && pausedByUser);
      button.textContent = playing ? "❚❚ pause" : "▶ play";
      button.setAttribute("aria-label", (playing ? "Pause " : "Play ") + (video.getAttribute("aria-label") || "video"));
    }

    function play() {
      var attempt = video.play();
      if (attempt && attempt.catch) {
        attempt.catch(function () {
          // Blocked by the browser's autoplay policy: wait for a click.
          pausedByUser = true;
          render();
        });
      }
    }

    function toggle() {
      if (video.paused) {
        pausedByUser = false;
        play();
      } else {
        pausedByUser = true;
        video.pause();
      }
    }

    button.addEventListener("click", function (e) {
      e.stopPropagation();
      toggle();
    });
    video.addEventListener("click", toggle);
    video.addEventListener("play", render);
    video.addEventListener("pause", render);

    if ("IntersectionObserver" in window) {
      new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          var nowVisible = entry.isIntersecting;
          if (nowVisible && !visible && !pausedByUser) {
            // Start each viewing from the top so the demo reads in order.
            try { video.currentTime = 0; } catch (e) {}
            play();
          } else if (!nowVisible && visible && !video.paused) {
            video.pause();
          }
          visible = nowVisible;
        });
      }, { threshold: 0.35 }).observe(video);
    } else if (!pausedByUser) {
      play();
    }

    render();
  }

  Array.prototype.forEach.call(videos, setup);
})();
