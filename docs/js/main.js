/* ==========================================================================
   TRAININGIFY - INTERACTIVE LOGIC & SIMULATORS
   ========================================================================== */

document.addEventListener('DOMContentLoaded', () => {

    // 1. STICKY NAVBAR
    const navbar = document.getElementById('navbar');
    window.addEventListener('scroll', () => {
        if (window.scrollY > 50) {
            navbar.classList.add('scrolled');
        } else {
            navbar.classList.remove('scrolled');
        }
    });

    // 2. MOBILE MENU TOGGLE
    const menuToggle = document.getElementById('menu-toggle');
    const navLinks = document.getElementById('nav-links');
    
    if (menuToggle && navLinks) {
        menuToggle.addEventListener('click', () => {
            navLinks.classList.toggle('active');
            const icon = menuToggle.querySelector('i');
            if (navLinks.classList.contains('active')) {
                icon.className = 'fa-solid fa-xmark';
            } else {
                icon.className = 'fa-solid fa-bars';
            }
        });

        // Close menu when clicking a link
        navLinks.querySelectorAll('a').forEach(link => {
            link.addEventListener('click', () => {
                navLinks.classList.remove('active');
                menuToggle.querySelector('i').className = 'fa-solid fa-bars';
            });
        });
    }

    // 3. INTERACTIVE COUPLING SIMULATOR (PLAYGROUND)
    const powerSlider = document.getElementById('power-slider');
    const powerVal = document.getElementById('power-val');
    const hrVal = document.getElementById('hr-val');
    const cadenceVal = document.getElementById('cadence-val');
    const zoneName = document.getElementById('zone-name');
    const zoneIndicator = document.getElementById('zone-indicator');
    
    const fanBlades = document.getElementById('fan-blades');
    const fanAirflow = document.getElementById('fan-airflow');
    const fanStatus = document.getElementById('fan-status');
    
    const virtualRoom = document.getElementById('virtual-room');
    const sweatStatus = document.getElementById('sweat-status');
    const sweatContainer = document.getElementById('sweat-container');

    let sweatInterval = null;
    let currentZoneNum = 0;

    // Vocal synthesis speech helper to announce zone shifts and keep athlete motivated
    function speakZoneChange(zoneNum) {
        if (zoneNum === currentZoneNum) return;
        currentZoneNum = zoneNum;
        
        if ('speechSynthesis' in window) {
            window.speechSynthesis.cancel(); // Stop current speech to announce immediately
            
            let textToSpeak = "";
            switch(zoneNum) {
                case 1:
                    textToSpeak = "Zone 1. Récupération active. Relâchez l'effort et respirez profondément.";
                    break;
                case 2:
                    textToSpeak = "Zone 2. Endurance de base. Gardez ce rythme régulier.";
                    break;
                case 3:
                    textToSpeak = "Zone 3. Tempo. Concentrez-vous, la sueur commence à venir.";
                    break;
                case 4:
                    textToSpeak = "Zone 4. Seuil lactique. C'est ici que l'effort paye. Accrochez-vous !";
                    break;
                case 5:
                    textToSpeak = "Zone 5. V O 2 Max. Dépassez vos limites, donnez tout ce que vous avez !";
                    break;
                case 6:
                    textToSpeak = "Zone 6. Capacité anaérobie. Tout à droite ! Écrasez les pédales, allez, allez !";
                    break;
            }
            
            if (textToSpeak) {
                const utterance = new SpeechSynthesisUtterance(textToSpeak);
                utterance.lang = 'fr-FR';
                utterance.rate = 1.05;
                window.speechSynthesis.speak(utterance);
            }
        }
    }

    if (powerSlider) {
        // Run initial update
        updateSimulator(parseInt(powerSlider.value));

        // Listen for slider changes
        powerSlider.addEventListener('input', (e) => {
            updateSimulator(parseInt(e.target.value));
        });
    }

    function updateSimulator(power) {
        // Update power text display
        powerVal.textContent = power;

        // 1. Simulate Heart Rate (BPM) based on Power (50W to 500W)
        // Base HR is 75 BPM, max is 195 BPM
        const hrPercent = (power - 50) / 450;
        const simulatedHR = Math.round(75 + (120 * hrPercent));
        hrVal.textContent = simulatedHR;

        // 2. Simulate Cadence (RPM)
        // Usually rides between 80 and 105 RPM depending on power
        const simulatedCadence = Math.round(82 + (20 * hrPercent) + (Math.sin(Date.now() / 1000) * 1));
        cadenceVal.textContent = simulatedCadence;

        // 3. Determine Training Zones (FTP assumed to be 250W)
        let zoneText = "";
        let sweatText = "";
        let sweatFrequency = 0; // ms between drops
        let zoneNum = 1;

        if (power < 138) {
            zoneText = "Zone 1 (Récupération Active)";
            sweatText = "Nulle (Échauffement)";
            zoneNum = 1;
            zoneIndicator.style.background = "rgba(255, 255, 255, 0.03)";
            zoneIndicator.style.borderColor = "rgba(255, 255, 255, 0.1)";
        } else if (power < 188) {
            zoneText = "Zone 2 (Endurance de Base)";
            sweatText = "Très légère";
            zoneNum = 2;
            zoneIndicator.style.background = "rgba(255, 255, 255, 0.05)";
            zoneIndicator.style.borderColor = "rgba(255, 255, 255, 0.15)";
        } else if (power < 226) {
            zoneText = "Zone 3 (Tempo / Rythme)";
            sweatText = "Tempérée (Début de sudation)";
            zoneNum = 3;
            zoneIndicator.style.background = "rgba(255, 255, 255, 0.08)";
            zoneIndicator.style.borderColor = "rgba(255, 255, 255, 0.2)";
            sweatFrequency = 2000; // Sweat starts
        } else if (power < 263) {
            zoneText = "Zone 4 (Seuil Lactique / FTP)";
            sweatText = "Intense";
            zoneNum = 4;
            zoneIndicator.style.background = "rgba(255, 85, 0, 0.08)";
            zoneIndicator.style.borderColor = "rgba(255, 85, 0, 0.2)";
            sweatFrequency = 900;
        } else if (power < 320) {
            zoneText = "Zone 5 (VO2 Max / PMA)";
            sweatText = "Ruisselante";
            zoneNum = 5;
            zoneIndicator.style.background = "rgba(255, 42, 95, 0.08)";
            zoneIndicator.style.borderColor = "rgba(255, 42, 95, 0.2)";
            sweatFrequency = 450;
        } else {
            zoneText = "Zone 6 (Capacité Anaérobie / Sprint)";
            sweatText = "Extrême (Sueur maximale)";
            zoneNum = 6;
            zoneIndicator.style.background = "rgba(255, 0, 0, 0.1)";
            zoneIndicator.style.borderColor = "rgba(255, 0, 0, 0.3)";
            sweatFrequency = 200;
        }

        zoneName.textContent = zoneText;
        if (sweatStatus) {
            sweatStatus.textContent = `Sudation : ${sweatText}`;
        }
        
        // Speak zone changes to keep motivation
        speakZoneChange(zoneNum);

        // 4. Update Smart Fan speed and airflow
        // Map power output to fan speed
        let fanPercentage = 0;
        let animationDuration = "0s"; // Off
        
        if (power < 80) {
            fanPercentage = 0;
            animationDuration = "0s";
            fanAirflow.style.opacity = "0";
        } else if (power < 150) {
            fanPercentage = 25;
            animationDuration = "2s";
            fanAirflow.style.opacity = "0.2";
        } else if (power < 230) {
            fanPercentage = 50;
            animationDuration = "0.9s";
            fanAirflow.style.opacity = "0.5";
        } else if (power < 300) {
            fanPercentage = 75;
            animationDuration = "0.4s";
            fanAirflow.style.opacity = "0.8";
        } else {
            fanPercentage = 100;
            animationDuration = "0.15s";
            fanAirflow.style.opacity = "1";
        }

        fanStatus.textContent = `Débit : ${fanPercentage === 0 ? 'Arrêt' : fanPercentage + '%'}`;
        
        if (fanPercentage > 0) {
            fanBlades.style.setProperty('--fan-speed', animationDuration);
            fanBlades.classList.add('spinning');
            
            // Speed up airflow lines animation by changing styling
            const lines = fanAirflow.querySelectorAll('.airflow-line');
            lines.forEach((line, idx) => {
                line.style.opacity = (fanPercentage / 100).toString();
                line.style.animation = `sweat-drip ${animationDuration === "2s" ? "1.5s" : "0.5s"} linear infinite`;
                line.style.animationDelay = `${idx * 0.2}s`;
            });
        } else {
            fanBlades.classList.remove('spinning');
        }

        // 5. Manage Sweat Generation
        clearInterval(sweatInterval);
        if (sweatFrequency > 0) {
            sweatInterval = setInterval(createSweatDrop, sweatFrequency);
        }
    }

    function createSweatDrop() {
        if (!sweatContainer) return;
        
        const drop = document.createElement('div');
        drop.className = 'sweat-drop';
        
        // Random horizontal position near the cyclist icon center
        const randomX = 40 + Math.random() * 20; // percent 40% - 60%
        drop.style.left = `${randomX}%`;
        drop.style.top = '30%'; // Spawns near the cyclist silhouette
        
        // Random falling speed
        const duration = 0.8 + Math.random() * 0.8;
        drop.style.animation = `sweat-drip ${duration}s linear forwards`;
        
        sweatContainer.appendChild(drop);
        
        // Clean up drop after animation finishes
        setTimeout(() => {
            drop.remove();
        }, duration * 1000);
    }

    // 4. SCREENSHOT CAROUSEL
    const screenshotCarousel = document.querySelector('[data-screenshot-carousel]');

    if (screenshotCarousel) {
        const carouselTrack = screenshotCarousel.querySelector('[data-carousel-track]');
        const carouselSlides = Array.from(screenshotCarousel.querySelectorAll('.carousel-slide'));
        const previousButton = screenshotCarousel.querySelector('[data-carousel-prev]');
        const nextButton = screenshotCarousel.querySelector('[data-carousel-next]');
        const dotsContainer = screenshotCarousel.querySelector('[data-carousel-dots]');
        const counter = screenshotCarousel.querySelector('[data-carousel-counter]');
        const reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;

        let currentSlide = 0;
        let autoPlayTimer = null;
        let touchStartX = null;

        carouselSlides.forEach((slide, index) => {
            slide.setAttribute('aria-hidden', index === 0 ? 'false' : 'true');
        });

        const dots = carouselSlides.map((_, index) => {
            const dot = document.createElement('button');
            dot.type = 'button';
            dot.className = 'carousel-dot';
            dot.setAttribute('role', 'tab');
            dot.setAttribute('aria-label', `Afficher la capture ${index + 1}`);
            dot.addEventListener('click', () => goToSlide(index));
            dotsContainer.appendChild(dot);
            return dot;
        });

        function renderCarousel() {
            carouselTrack.style.transform = `translateX(-${currentSlide * 100}%)`;

            carouselSlides.forEach((slide, index) => {
                const isActive = index === currentSlide;
                slide.classList.toggle('is-active', isActive);
                slide.setAttribute('aria-hidden', isActive ? 'false' : 'true');
            });

            dots.forEach((dot, index) => {
                const isActive = index === currentSlide;
                dot.classList.toggle('is-active', isActive);
                dot.setAttribute('aria-selected', isActive ? 'true' : 'false');
                dot.tabIndex = isActive ? 0 : -1;
            });

            if (counter) {
                counter.textContent = `${currentSlide + 1} / ${carouselSlides.length}`;
            }
        }

        function stopAutoPlay() {
            if (autoPlayTimer) {
                window.clearInterval(autoPlayTimer);
                autoPlayTimer = null;
            }
        }

        function startAutoPlay() {
            stopAutoPlay();

            if (!reducedMotion && carouselSlides.length > 1) {
                autoPlayTimer = window.setInterval(() => {
                    goToSlide(currentSlide + 1, false);
                }, 6000);
            }
        }

        function goToSlide(index, restartAutoPlay = true) {
            currentSlide = (index + carouselSlides.length) % carouselSlides.length;
            renderCarousel();

            if (restartAutoPlay) {
                startAutoPlay();
            }
        }

        previousButton?.addEventListener('click', () => goToSlide(currentSlide - 1));
        nextButton?.addEventListener('click', () => goToSlide(currentSlide + 1));

        screenshotCarousel.addEventListener('mouseenter', stopAutoPlay);
        screenshotCarousel.addEventListener('mouseleave', startAutoPlay);
        screenshotCarousel.addEventListener('focusin', stopAutoPlay);
        screenshotCarousel.addEventListener('focusout', event => {
            if (!screenshotCarousel.contains(event.relatedTarget)) {
                startAutoPlay();
            }
        });

        screenshotCarousel.addEventListener('keydown', event => {
            if (event.key === 'ArrowLeft') {
                event.preventDefault();
                goToSlide(currentSlide - 1);
            } else if (event.key === 'ArrowRight') {
                event.preventDefault();
                goToSlide(currentSlide + 1);
            }
        });

        screenshotCarousel.addEventListener('touchstart', event => {
            touchStartX = event.changedTouches[0]?.clientX ?? null;
            stopAutoPlay();
        }, { passive: true });

        screenshotCarousel.addEventListener('touchend', event => {
            if (touchStartX === null) return;

            const touchEndX = event.changedTouches[0]?.clientX ?? touchStartX;
            const swipeDistance = touchEndX - touchStartX;

            if (Math.abs(swipeDistance) > 45) {
                goToSlide(currentSlide + (swipeDistance < 0 ? 1 : -1));
            } else {
                startAutoPlay();
            }

            touchStartX = null;
        }, { passive: true });

        renderCarousel();
        startAutoPlay();
    }

    // 5. SCROLL REVEAL ANIMATIONS (Intersection Observer)
    const revealElements = document.querySelectorAll('.feature-card, .ecosystem-content, .ecosystem-visual, .playground-card, .screenshot-card');
    
    if ('IntersectionObserver' in window && revealElements.length > 0) {
        // Add reveal class initial state
        revealElements.forEach(el => el.classList.add('reveal'));
        
        const revealObserver = new IntersectionObserver((entries, observer) => {
            entries.forEach(entry => {
                if (entry.isIntersecting) {
                    entry.target.classList.add('active');
                    observer.unobserve(entry.target); // Reveal only once
                }
            });
        }, {
            threshold: 0.15,
            rootMargin: '0px 0px -50px 0px'
        });

        revealElements.forEach(el => revealObserver.observe(el));
    }
});
